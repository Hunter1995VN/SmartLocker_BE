namespace Infrastructure.Services;

using System.Collections.Concurrent;
using Application.DTOs.Booking;
using Application.DTOs.Common;
using Application.DTOs.Payment;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class BookingService : IBookingService
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _bookingLocks = new();
    private readonly ISmartLockerDbContext _context;
    private readonly IPricingService _pricingService;
    private readonly IPaymentGatewayService _paymentGateway;
    private readonly ILogger<BookingService> _logger;

    public BookingService(ISmartLockerDbContext context, IPricingService pricingService,
        IPaymentGatewayService paymentGateway, ILogger<BookingService> logger)
    {
        _context = context;
        _pricingService = pricingService;
        _paymentGateway = paymentGateway;
        _logger = logger;
    }

    /// <summary>
    /// UC-T02: Tạo booking mới → tạo payment → trả link thanh toán PayOS
    /// </summary>
    public async Task<ApiResponse<CreateBookingResponse>> CreateBookingAsync(Guid userId, CreateBookingRequest request)
    {
        // Validate size
        if (!Enum.TryParse<LockerSize>(request.Size, true, out var lockerSize))
            return ApiResponse<CreateBookingResponse>.ErrorResponse("Kích thước không hợp lệ. Chọn S, M, hoặc L.");

        if (request.StartAt >= request.EndAt)
            return ApiResponse<CreateBookingResponse>.ErrorResponse("Thời gian kết thúc phải lớn hơn thời gian bắt đầu.");

        var durationHours = (request.EndAt - request.StartAt).TotalHours;

        // Check user eligibility (BR-P08: OverdueDebt > 0 → blocked)
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null || user.Status != UserStatus.ACTIVE)
            return ApiResponse<CreateBookingResponse>.ErrorResponse("Tài khoản không hợp lệ hoặc chưa được kích hoạt.");
        if (user.OverdueDebt > 0)
            return ApiResponse<CreateBookingResponse>.ErrorResponse("Bạn có khoản nợ quá hạn chưa thanh toán. Vui lòng thanh toán trước.");

        // Check station
        var station = await _context.Stations.FirstOrDefaultAsync(s => s.Id == request.StationId);
        if (station == null || station.Status != StationStatus.ACTIVE)
            return ApiResponse<CreateBookingResponse>.ErrorResponse("Trạm không hoạt động.");

        // Get pricing policy
        var policy = await _pricingService.GetActivePolicyAsync(request.StationId, lockerSize);
        if (policy == null)
            return ApiResponse<CreateBookingResponse>.ErrorResponse("Không có chính sách giá phù hợp.");

        if (durationHours < policy.MinHours || durationHours > policy.MaxHours)
            return ApiResponse<CreateBookingResponse>.ErrorResponse($"Thời gian đặt phải từ {policy.MinHours} đến {policy.MaxHours} giờ.");

        // Transaction with UPDLOCK for slot availability
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // Count bookable lockers
                var totalLockers = await _context.Lockers
                    .Where(l => l.StationId == request.StationId && l.Size == lockerSize &&
                                l.BusinessStatus == LockerBusinessStatus.AVAILABLE &&
                                l.HealthStatus == LockerHealthStatus.HEALTHY)
                    .CountAsync();

                // Count overlapping active bookings with lock
                var activeCount = await _context.Bookings
                    .FromSqlRaw(@"SELECT * FROM Bookings WITH (UPDLOCK, HOLDLOCK) 
                                  WHERE StationId = {0} AND Size = {1} 
                                  AND Status IN ('PENDING_PAYMENT', 'CONFIRMED', 'STORED') 
                                  AND StartAt < {2} AND EndAt > {3}",
                                  request.StationId, lockerSize.ToString(), request.EndAt, request.StartAt)
                    .CountAsync();

                if (totalLockers - activeCount <= 0)
                    return ApiResponse<CreateBookingResponse>.ErrorResponse("Không còn tủ trống cho kích thước này.");

                // Generate booking
                var bookingCode = "BK-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + new Random().Next(1000, 9999);
                var amount = await _pricingService.CalculateBookingPriceAsync(request.StationId, lockerSize, request.StartAt, request.EndAt);
                var expiresAt = DateTime.UtcNow.AddMinutes(10);

                // PayOS orderCode phải là long — dùng timestamp + random
                var orderCode = long.Parse(DateTime.UtcNow.ToString("yyMMddHHmm") + new Random().Next(1000, 9999).ToString());

                var booking = new Booking
                {
                    BookingCode = bookingCode,
                    UserId = userId,
                    StationId = request.StationId,
                    PricingPolicyId = policy.Id,
                    Size = lockerSize,
                    Status = BookingStatus.PENDING_PAYMENT,
                    StartAt = request.StartAt,
                    EndAt = request.EndAt,
                    PaymentExpiresAt = expiresAt,
                    BaseAmount = amount
                };
                _context.Bookings.Add(booking);

                var payment = new Payment
                {
                    BookingId = booking.Id,
                    Kind = PaymentKind.BASE,
                    Status = PaymentStatus.PENDING,
                    Amount = amount,
                    OrderCode = orderCode.ToString(),
                    ExpiresAt = expiresAt,
                    Gateway = "PAYOS"
                };
                _context.Payments.Add(payment);
                await _context.SaveChangesAsync();

                // Create PayOS payment link
                var paymentResult = await _paymentGateway.CreatePaymentLinkAsync(
                    orderCode, (int)amount, $"Dat tu {bookingCode}", "", "");

                payment.PaymentLink = paymentResult.CheckoutUrl;
                payment.GatewayTxnId = paymentResult.PaymentLinkId;
                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                return ApiResponse<CreateBookingResponse>.SuccessResponse(new CreateBookingResponse
                {
                    BookingId = booking.Id,
                    BookingCode = booking.BookingCode,
                    Amount = amount,
                    PaymentUrl = paymentResult.CheckoutUrl,
                    PaymentExpiresAt = expiresAt,
                    QrCode = paymentResult.QrCode,
                    AccountNumber = paymentResult.AccountNumber,
                    AccountName = paymentResult.AccountName,
                    Bin = paymentResult.Bin
                }, "Tạo đặt tủ thành công. Vui lòng thanh toán trong 10 phút.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error creating booking");
                return ApiResponse<CreateBookingResponse>.ErrorResponse("Lỗi hệ thống khi tạo đặt tủ.");
            }
        });
    }

    /// <summary>
    /// UC-T05: Xem chi tiết booking
    /// </summary>
    public async Task<ApiResponse<BookingDto>> GetBookingByIdAsync(Guid userId, Guid bookingId)
    {
        var booking = await _context.Bookings
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.Payments)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null) return ApiResponse<BookingDto>.ErrorResponse("Không tìm thấy đặt tủ.");
        if (booking.UserId != userId) return ApiResponse<BookingDto>.ErrorResponse("Không có quyền truy cập.");

        var dto = MapToBookingDto(booking);

        // Calculate derived fields
        if (booking.IsOverdue && booking.PricingPolicyId != null)
        {
            var policy = await _context.PricingPolicies.FindAsync(booking.PricingPolicyId);
            if (policy != null) dto.CurrentOverdueFee = _pricingService.CalculateOverdueFee(booking, policy);
        }

        if (dto.CanCancel)
        {
            if (booking.PricingPolicy == null && booking.PricingPolicyId != null)
                booking.PricingPolicy = await _context.PricingPolicies.FindAsync(booking.PricingPolicyId);
            dto.EstimatedRefundAmount = _pricingService.CalculateRefundAmount(booking);
        }

        return ApiResponse<BookingDto>.SuccessResponse(dto);
    }

    /// <summary>
    /// UC-T06: Danh sách booking của user (phân trang, filter theo status)
    /// </summary>
    public async Task<ApiResponse<PagedResult<BookingListItemDto>>> GetUserBookingsAsync(
        Guid userId, string? status, int page = 1, int pageSize = 20)
    {
        var query = _context.Bookings.Include(b => b.Station).Where(b => b.UserId == userId);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<BookingStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(b => b.Status == parsedStatus);
        }

        var total = await query.CountAsync();
        var entities = await query.OrderByDescending(b => b.CreatedAt)
            .Include(b => b.Locker)
            .Include(b => b.AccessCredential)
            .Include(b => b.Payments)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = entities.Select(b =>
        {
            var basePayment = b.Payments.FirstOrDefault(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PAID);
            var confirmedAt = basePayment?.PaidAt ?? b.CreatedAt;
            var holdExpiresAt = b.Status == BookingStatus.CONFIRMED ? (DateTime?)confirmedAt.AddMinutes(30) : null;
            var duration = Math.Max(1, Math.Round((b.EndAt - b.StartAt).TotalHours, 1));

            return new BookingListItemDto
            {
                Id = b.Id,
                BookingCode = b.BookingCode,
                StationName = b.Station.Name,
                Size = b.Size.ToString(),
                Status = b.Status.ToString(),
                StartAt = b.StartAt,
                EndAt = b.EndAt,
                CheckedInAt = b.CheckedInAt,
                HoldExpiresAt = holdExpiresAt,
                DurationHours = duration,
                LockerCode = b.Locker != null ? b.Locker.LockerCode : null,
                AccessCode = b.AccessCredential != null ? b.AccessCredential.OfflinePayload : null,
                BaseAmount = b.BaseAmount,
                IsOverdue = b.IsOverdue,
                CreatedAt = b.CreatedAt
            };
        }).ToList();

        return ApiResponse<PagedResult<BookingListItemDto>>.SuccessResponse(new PagedResult<BookingListItemDto>
        {
            Items = items,
            TotalCount = total,
            PageSize = pageSize,
            Page = page
        });
    }

    /// <summary>
    /// UC-T07: Gia hạn booking → tạo payment gia hạn → trả link PayOS
    /// Bảo vệ chống concurrency double-click (TC_BKO_20) bằng SemaphoreSlim lock & Idempotency check.
    /// </summary>
    public async Task<ApiResponse<CreateBookingResponse>> ExtendBookingAsync(Guid userId, Guid bookingId, ExtendBookingRequest request)
    {
        var semaphore = _bookingLocks.GetOrAdd(bookingId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            var booking = await _context.Bookings.Include(b => b.PricingPolicy).FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không tìm thấy đặt tủ.");
            if (booking.UserId != userId) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không có quyền.");
            if (booking.Status != BookingStatus.STORED) return ApiResponse<CreateBookingResponse>.ErrorResponse("Chỉ có thể gia hạn khi đang gửi đồ (trạng thái STORED).");
            if (request.NewEndAt <= booking.EndAt) return ApiResponse<CreateBookingResponse>.ErrorResponse("Thời gian gia hạn phải lớn hơn thời gian kết thúc hiện tại.");

            // TC_BKO_20: Kiểm tra xem đã có giao dịch gia hạn PENDING nào được tạo gần đây không (trong vòng 15 phút)
            // Nếu có, tái sử dụng link cũ thay vì tạo 2 link PayOS trùng lặp
            var existingPendingPayment = await _context.Payments
                .Where(p => p.BookingId == booking.Id 
                         && p.Kind == PaymentKind.EXTENSION 
                         && p.Status == PaymentStatus.PENDING)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (existingPendingPayment != null)
            {
                if (existingPendingPayment.CreatedAt >= DateTime.UtcNow.AddMinutes(-15) && !string.IsNullOrEmpty(existingPendingPayment.PaymentLink))
                {
                    _logger.LogInformation("TC_BKO_20: Reusing existing pending extension payment for booking {BookingId}, orderCode {OrderCode}", 
                        booking.Id, existingPendingPayment.OrderCode);
                    return ApiResponse<CreateBookingResponse>.SuccessResponse(new CreateBookingResponse
                    {
                        BookingId = booking.Id,
                        BookingCode = booking.BookingCode,
                        Amount = existingPendingPayment.Amount,
                        PaymentUrl = existingPendingPayment.PaymentLink
                    }, "Đã có yêu cầu thanh toán gia hạn đang chờ xử lý. Đang chuyển hướng...");
                }
                else
                {
                    existingPendingPayment.Status = PaymentStatus.EXPIRED;
                    await _context.SaveChangesAsync();
                }
            }

            var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
            if (policy == null) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không có chính sách giá.");

            var totalDuration = (request.NewEndAt - booking.StartAt).TotalHours;
            if (totalDuration > policy.MaxHours)
                return ApiResponse<CreateBookingResponse>.ErrorResponse($"Tổng thời gian không được vượt quá {policy.MaxHours} giờ.");

            var amount = await _pricingService.CalculateExtensionPriceAsync(booking, request.NewEndAt);
            if (booking.IsOverdue) amount += _pricingService.CalculateOverdueFee(booking, policy);

            var orderCode = long.Parse(DateTime.UtcNow.ToString("yyMMddHHmm") + new Random().Next(1000, 9999).ToString());
            var payment = new Payment
            {
                BookingId = booking.Id,
                Kind = PaymentKind.EXTENSION,
                Status = PaymentStatus.PENDING,
                Amount = amount,
                OrderCode = orderCode.ToString(),
                Gateway = "PAYOS",
                WebhookPayload = $"{{\"NewEndAt\":\"{request.NewEndAt:O}\"}}"
            };
            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();

            var (checkoutUrl, paymentLinkId) = await _paymentGateway.CreatePaymentLinkAsync(
                orderCode, (int)amount, $"Gia han {booking.BookingCode}", "", "");

            payment.PaymentLink = checkoutUrl;
            payment.GatewayTxnId = paymentLinkId;
            await _context.SaveChangesAsync();

            return ApiResponse<CreateBookingResponse>.SuccessResponse(new CreateBookingResponse
            {
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                Amount = amount,
                PaymentUrl = checkoutUrl
            }, "Tạo link thanh toán gia hạn thành công.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// UC-T08: Hủy booking + tính hoàn tiền theo BR-T06
    /// </summary>
    public async Task<ApiResponse<BookingDto>> CancelBookingAsync(Guid userId, Guid bookingId, CancelBookingRequest request)
    {
        var booking = await _context.Bookings.Include(b => b.PricingPolicy).FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return ApiResponse<BookingDto>.ErrorResponse("Không tìm thấy đặt tủ.");
        if (booking.UserId != userId) return ApiResponse<BookingDto>.ErrorResponse("Không có quyền.");
        if (booking.Status != BookingStatus.PENDING_PAYMENT && booking.Status != BookingStatus.CONFIRMED)
            return ApiResponse<BookingDto>.ErrorResponse("Không thể hủy đặt tủ ở trạng thái hiện tại. Chỉ hủy được khi PENDING_PAYMENT hoặc CONFIRMED.");

        if (booking.PricingPolicy == null && booking.PricingPolicyId != null)
            booking.PricingPolicy = await _context.PricingPolicies.FindAsync(booking.PricingPolicyId);

        var refundAmount = _pricingService.CalculateRefundAmount(booking);
        booking.Status = BookingStatus.CANCELLED;
        booking.CancellationReason = request.Reason;

        var timeUntilStart = (booking.StartAt - DateTime.UtcNow).TotalHours;
        booking.CancelPolicyRate = timeUntilStart > (booking.PricingPolicy?.RefundCutoffHours ?? 2)
            ? (booking.PricingPolicy?.RefundRateEarly ?? 1.00m)
            : (timeUntilStart > 0 ? (booking.PricingPolicy?.RefundRateLate ?? 0.50m) : 0);

        // Create refund record if applicable
        if (refundAmount > 0)
        {
            _context.Payments.Add(new Payment
            {
                BookingId = booking.Id,
                Kind = PaymentKind.REFUND,
                Status = PaymentStatus.PAID,
                Amount = refundAmount,
                OrderCode = "REF-" + Guid.NewGuid().ToString("N")[..12].ToUpper(),
                PaidAt = DateTime.UtcNow,
                Gateway = "PAYOS"
            });
        }

        // Cancel pending payments
        var pendingPayments = await _context.Payments
            .Where(p => p.BookingId == booking.Id && p.Status == PaymentStatus.PENDING)
            .ToListAsync();
        foreach (var p in pendingPayments)
        {
            p.Status = PaymentStatus.EXPIRED;
            // Also cancel PayOS payment link if exists
            if (long.TryParse(p.OrderCode, out var code))
                await _paymentGateway.CancelPaymentLinkAsync(code, "Booking cancelled");
        }

        await _context.SaveChangesAsync();

        return await GetBookingByIdAsync(userId, bookingId);
    }

    /// <summary>
    /// UC-T09: Thanh toán phụ phí quá hạn
    /// Bảo vệ chống concurrency double-click (TC_BKO_20) bằng SemaphoreSlim lock & Idempotency check.
    /// </summary>
    public async Task<ApiResponse<CreateBookingResponse>> PayOverdueFeeAsync(Guid userId, Guid bookingId)
    {
        var semaphore = _bookingLocks.GetOrAdd(bookingId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            var booking = await _context.Bookings.Include(b => b.PricingPolicy).FirstOrDefaultAsync(b => b.Id == bookingId);
            if (booking == null) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không tìm thấy đặt tủ.");
            if (booking.UserId != userId) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không có quyền.");
            if (booking.Status != BookingStatus.STORED || !booking.IsOverdue)
                return ApiResponse<CreateBookingResponse>.ErrorResponse("Đặt tủ không có phí quá hạn.");

            var existingPendingPayment = await _context.Payments
                .Where(p => p.BookingId == booking.Id 
                         && p.Kind == PaymentKind.OVERDUE 
                         && p.Status == PaymentStatus.PENDING)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (existingPendingPayment != null)
            {
                if (existingPendingPayment.CreatedAt >= DateTime.UtcNow.AddMinutes(-15) && !string.IsNullOrEmpty(existingPendingPayment.PaymentLink))
                {
                    _logger.LogInformation("TC_BKO_20: Reusing existing pending overdue payment for booking {BookingId}, orderCode {OrderCode}", 
                        booking.Id, existingPendingPayment.OrderCode);
                    return ApiResponse<CreateBookingResponse>.SuccessResponse(new CreateBookingResponse
                    {
                        BookingId = booking.Id,
                        BookingCode = booking.BookingCode,
                        Amount = existingPendingPayment.Amount,
                        PaymentUrl = existingPendingPayment.PaymentLink
                    }, "Đã có yêu cầu thanh toán phụ phí đang chờ xử lý. Đang chuyển hướng...");
                }
                else
                {
                    existingPendingPayment.Status = PaymentStatus.EXPIRED;
                    await _context.SaveChangesAsync();
                }
            }

            var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
            if (policy == null) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không có chính sách giá.");

            var amount = _pricingService.CalculateOverdueFee(booking, policy);

            var orderCode = long.Parse(DateTime.UtcNow.ToString("yyMMddHHmm") + new Random().Next(1000, 9999).ToString());
            var payment = new Payment
            {
                BookingId = booking.Id,
                Kind = PaymentKind.OVERDUE,
                Status = PaymentStatus.PENDING,
                Amount = amount,
                OrderCode = orderCode.ToString(),
                Gateway = "PAYOS"
            };
            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();

            var (checkoutUrl, paymentLinkId) = await _paymentGateway.CreatePaymentLinkAsync(
                orderCode, (int)amount, $"Qua han {booking.BookingCode}", "", "");

            payment.PaymentLink = checkoutUrl;
            payment.GatewayTxnId = paymentLinkId;
            await _context.SaveChangesAsync();

            return ApiResponse<CreateBookingResponse>.SuccessResponse(new CreateBookingResponse
            {
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                Amount = amount,
                PaymentUrl = checkoutUrl
            }, "Tạo link thanh toán phụ phí quá hạn thành công.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// UC-T04 / Check-in: Khách đến trạm mở tủ nhận chỗ.
    /// Bắt đầu tính giờ đặt tủ thực tế:
    /// - StartAt = CheckedInAt (thời điểm nhận tủ)
    /// - EndAt = StartAt + Duration
    /// - Gán ô tủ vật lý khả dụng (Assign-late BR-A04)
    /// - Status -> STORED
    /// </summary>
    public async Task<ApiResponse<BookingDto>> CheckInBookingAsync(Guid userId, Guid bookingId)
    {
        var booking = await _context.Bookings
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.Payments)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
            return ApiResponse<BookingDto>.ErrorResponse("Không tìm thấy thông tin đặt tủ.");

        if (booking.UserId != userId)
            return ApiResponse<BookingDto>.ErrorResponse("Bạn không có quyền thực hiện thao tác trên đơn này.");

        // Nếu đã check-in rồi, trả về luôn thông tin
        if (booking.Status == BookingStatus.STORED)
            return ApiResponse<BookingDto>.SuccessResponse(MapToBookingDto(booking), "Tủ đã được mở và đang trong quá trình lưu trữ.");

        if (booking.Status != BookingStatus.CONFIRMED)
            return ApiResponse<BookingDto>.ErrorResponse($"Không thể nhận tủ ở trạng thái hiện tại ({booking.Status}). Chỉ nhận tủ khi đơn ở trạng thái CONFIRMED.");

        // Kiểm tra thời gian giữ chỗ 30 phút
        var basePayment = booking.Payments.FirstOrDefault(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PAID);
        var confirmedAt = basePayment?.PaidAt ?? booking.CreatedAt;
        var holdExpiresAt = confirmedAt.AddMinutes(30);

        if (DateTime.UtcNow > holdExpiresAt)
        {
            booking.Status = BookingStatus.NO_SHOW;
            booking.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return ApiResponse<BookingDto>.ErrorResponse("Đã quá 30 phút giữ chỗ. Đơn đặt tủ đã hết hạn (No-show).");
        }

        // Tính duration khách đã đặt trước đó
        var durationHours = (booking.EndAt - booking.StartAt).TotalHours;
        if (durationHours <= 0) durationHours = 1;

        var now = DateTime.UtcNow;
        booking.CheckedInAt = now;
        booking.StartAt = now;
        booking.EndAt = now.AddHours(durationHours);
        booking.Status = BookingStatus.STORED;
        booking.UpdatedAt = now;

        // Cấp ô tủ vật lý nếu chưa gán (Assign-late BR-A04)
        if (booking.LockerId == null)
        {
            var availableLocker = await _context.Lockers
                .FirstOrDefaultAsync(l => l.StationId == booking.StationId
                                       && l.Size == booking.Size
                                       && l.BusinessStatus == LockerBusinessStatus.AVAILABLE
                                       && l.HealthStatus == LockerHealthStatus.HEALTHY);

            if (availableLocker != null)
            {
                booking.LockerId = availableLocker.Id;
                booking.Locker = availableLocker;
                availableLocker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
                availableLocker.UpdatedAt = now;
            }
        }
        else if (booking.Locker != null)
        {
            booking.Locker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
            booking.Locker.UpdatedAt = now;
        }

        // Cập nhật QrExpiresAt cho AccessCredential
        if (booking.AccessCredential != null)
        {
            booking.AccessCredential.QrExpiresAt = booking.EndAt;
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Booking {Code} checked in at {Time}. New EndAt: {EndAt}. Assigned Locker: {Locker}",
            booking.BookingCode, booking.CheckedInAt, booking.EndAt, booking.Locker?.LockerCode ?? "N/A");

        return ApiResponse<BookingDto>.SuccessResponse(MapToBookingDto(booking), "Nhận tủ thành công! Thời gian lưu trữ bắt đầu tính từ bây giờ.");
    }

    /// <summary>
    /// Check-out: Khách mở tủ lấy đồ và hoàn tất lưu trữ
    /// - CheckedOutAt = now
    /// - Status -> COMPLETED
    /// - Locker -> AVAILABLE
    /// </summary>
    public async Task<ApiResponse<BookingDto>> CheckOutBookingAsync(Guid userId, Guid bookingId)
    {
        var booking = await _context.Bookings
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.Payments)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
            return ApiResponse<BookingDto>.ErrorResponse("Không tìm thấy thông tin đặt tủ.");

        if (booking.UserId != userId)
            return ApiResponse<BookingDto>.ErrorResponse("Bạn không có quyền thực hiện thao tác trên đơn này.");

        if (booking.Status == BookingStatus.COMPLETED)
            return ApiResponse<BookingDto>.SuccessResponse(MapToBookingDto(booking), "Đơn đã hoàn tất trước đó.");

        if (booking.Status != BookingStatus.STORED)
            return ApiResponse<BookingDto>.ErrorResponse($"Không thể trả tủ ở trạng thái hiện tại ({booking.Status}).");

        var now = DateTime.UtcNow;
        booking.CheckedOutAt = now;
        booking.Status = BookingStatus.COMPLETED;
        booking.UpdatedAt = now;

        if (booking.Locker != null)
        {
            booking.Locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
            booking.Locker.UpdatedAt = now;
        }

        if (booking.AccessCredential != null)
        {
            booking.AccessCredential.IsActive = false;
            booking.AccessCredential.RevokedAt = now;
            booking.AccessCredential.RevokeReason = "Normal checkout completed";
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Booking {Code} completed checkout at {Time}.", booking.BookingCode, now);

        return ApiResponse<BookingDto>.SuccessResponse(MapToBookingDto(booking), "Trả tủ thành công. Cảm ơn bạn đã sử dụng dịch vụ!");
    }

    // === Private helpers ===

    private static BookingDto MapToBookingDto(Booking booking)
    {
        var basePayment = booking.Payments.FirstOrDefault(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PAID);
        var confirmedAt = basePayment?.PaidAt ?? booking.CreatedAt;
        var holdExpiresAt = booking.Status == BookingStatus.CONFIRMED ? (DateTime?)confirmedAt.AddMinutes(30) : null;
        var duration = Math.Max(1, Math.Round((booking.EndAt - booking.StartAt).TotalHours, 1));

        return new()
        {
            Id = booking.Id,
            BookingCode = booking.BookingCode,
            UserId = booking.UserId,
            StationId = booking.StationId,
            StationName = booking.Station.Name,
            StationAddress = booking.Station.Address,
            LockerCode = booking.Locker?.LockerCode,
            AccessCode = booking.AccessCredential?.OfflinePayload,
            QrPayload = booking.AccessCredential?.QrPayload,
            Size = booking.Size.ToString(),
            Status = booking.Status.ToString(),
            StartAt = booking.StartAt,
            EndAt = booking.EndAt,
            CheckedInAt = booking.CheckedInAt,
            CheckedOutAt = booking.CheckedOutAt,
            HoldExpiresAt = holdExpiresAt,
            DurationHours = duration,
            BaseAmount = booking.BaseAmount,
            IsOverdue = booking.IsOverdue,
            OverdueSince = booking.OverdueSince,
            ExtensionCount = booking.ExtensionCount,
            CancellationReason = booking.CancellationReason,
            CancelPolicyRate = booking.CancelPolicyRate,
            CreatedAt = booking.CreatedAt,
            Payments = booking.Payments.Select(p => new PaymentDto
            {
                Id = p.Id,
                BookingId = p.BookingId,
                Amount = p.Amount,
                Status = p.Status.ToString(),
                Kind = p.Kind.ToString(),
                Currency = p.Currency,
                OrderCode = p.OrderCode,
                Gateway = p.Gateway,
                PaymentLink = p.PaymentLink,
                PaidAt = p.PaidAt,
                RefundAmount = p.RefundAmount,
                RefundedAt = p.RefundedAt,
                CreatedAt = p.CreatedAt
            }).ToList(),
            CanExtend = booking.Status == BookingStatus.STORED,
            CanCancel = booking.Status == BookingStatus.PENDING_PAYMENT || booking.Status == BookingStatus.CONFIRMED
        };
    }
}
