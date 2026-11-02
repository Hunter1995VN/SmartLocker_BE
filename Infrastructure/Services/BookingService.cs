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
    private readonly IPaymentService _paymentService;
    private readonly ILogger<BookingService> _logger;

    public BookingService(ISmartLockerDbContext context, IPricingService pricingService,
        IPaymentGatewayService paymentGateway, IPaymentService paymentService, ILogger<BookingService> logger)
    {
        _context = context;
        _pricingService = pricingService;
        _paymentGateway = paymentGateway;
        _paymentService = paymentService;
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

        // UC-T07: Concurrency lock theo trạm + SQL Row Lock (UPDLOCK, HOLDLOCK) & Idempotency chống double-click
        var stationLock = _bookingLocks.GetOrAdd(request.StationId, _ => new SemaphoreSlim(1, 1));
        await stationLock.WaitAsync();
        try
        {
            var nowUtc = DateTime.UtcNow;
            var originalAmount = await _pricingService.CalculateBookingPriceAsync(request.StationId, lockerSize, request.StartAt, request.EndAt);
            var discountAmount = 0m;
            string? appliedVoucherCode = null;
            Voucher? voucherToUpdate = null;

            if (!string.IsNullOrWhiteSpace(request.VoucherCode))
            {
                var cleanCode = request.VoucherCode.Trim().ToUpperInvariant();
                var voucher = await _context.Vouchers.FirstOrDefaultAsync(v => v.Code.ToUpper() == cleanCode);
                if (voucher == null || !voucher.IsActive)
                    return ApiResponse<CreateBookingResponse>.ErrorResponse($"Mã giảm giá '{cleanCode}' không hợp lệ hoặc đã ngừng hoạt động.");
                if (nowUtc < voucher.StartDate || nowUtc > voucher.EndDate)
                    return ApiResponse<CreateBookingResponse>.ErrorResponse($"Mã giảm giá '{cleanCode}' đã hết hạn sử dụng.");
                if (voucher.UsedCount >= voucher.UsageLimit)
                    return ApiResponse<CreateBookingResponse>.ErrorResponse($"Mã giảm giá '{cleanCode}' đã hết lượt sử dụng.");
                if (originalAmount < voucher.MinOrderAmount)
                    return ApiResponse<CreateBookingResponse>.ErrorResponse($"Mã '{cleanCode}' yêu cầu đơn đặt tủ tối thiểu {voucher.MinOrderAmount:N0} VND.");

                decimal rawDisc;
                if (string.Equals(voucher.DiscountType, "PERCENTAGE", StringComparison.OrdinalIgnoreCase))
                {
                    rawDisc = Math.Round(originalAmount * (voucher.DiscountValue / 100m), 0);
                    if (voucher.MaxDiscountAmount.HasValue && voucher.MaxDiscountAmount.Value > 0)
                        rawDisc = Math.Min(rawDisc, voucher.MaxDiscountAmount.Value);
                }
                else
                {
                    rawDisc = voucher.DiscountValue;
                }

                discountAmount = Math.Min(rawDisc, Math.Max(0m, originalAmount - 2000m));
                appliedVoucherCode = voucher.Code;
                voucherToUpdate = voucher;
            }

            var finalAmount = Math.Max(2000m, originalAmount - discountAmount);

            // Kiểm tra Idempotency: nếu cùng user vừa tạo đơn PENDING_PAYMENT cho cùng trạm + cùng cỡ tủ + cùng số tiền trong 90 giây gần nhất
            var existingRecent = await _context.Bookings
                .Include(b => b.Payments)
                .Where(b => b.UserId == userId
                         && b.StationId == request.StationId
                         && b.Size == lockerSize
                         && b.BaseAmount == finalAmount
                         && b.Status == BookingStatus.PENDING_PAYMENT
                         && b.CreatedAt >= nowUtc.AddSeconds(-90)
                         && (b.PaymentExpiresAt == null || b.PaymentExpiresAt > nowUtc))
                .OrderByDescending(b => b.CreatedAt)
                .FirstOrDefaultAsync();

            if (existingRecent != null)
            {
                var existingPayment = existingRecent.Payments
                    .FirstOrDefault(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PENDING && !string.IsNullOrEmpty(p.PaymentLink));
                if (existingPayment != null && long.TryParse(existingPayment.OrderCode, out var existingOrderCode))
                {
                    _logger.LogInformation("UC-T07 Idempotency: Reusing existing pending booking {BookingCode} for user {UserId}", existingRecent.BookingCode, userId);
                    return ApiResponse<CreateBookingResponse>.SuccessResponse(new CreateBookingResponse
                    {
                        BookingId = existingRecent.Id,
                        BookingCode = existingRecent.BookingCode,
                        OriginalAmount = originalAmount,
                        DiscountAmount = discountAmount,
                        VoucherCode = appliedVoucherCode,
                        Amount = existingRecent.BaseAmount,
                        PaymentUrl = existingPayment.PaymentLink ?? "",
                        PaymentExpiresAt = existingRecent.PaymentExpiresAt ?? nowUtc.AddMinutes(10),
                        OrderCode = existingOrderCode
                    }, "Đơn giữ chỗ của bạn đã sẵn sàng. Vui lòng hoàn tất thanh toán.");
                }
            }

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

                    // Count overlapping active bookings with lock (ignore expired PENDING_PAYMENT holds)
                    var activeCount = await _context.Bookings
                        .FromSqlRaw(@"SELECT * FROM Bookings WITH (UPDLOCK, HOLDLOCK) 
                                      WHERE StationId = {0} AND Size = {1} 
                                      AND (Status IN ('CONFIRMED', 'STORED') OR (Status = 'PENDING_PAYMENT' AND (PaymentExpiresAt IS NULL OR PaymentExpiresAt > GETUTCDATE())))
                                      AND StartAt < {2} AND EndAt > {3}",
                                      request.StationId, lockerSize.ToString(), request.EndAt, request.StartAt)
                        .CountAsync();

                    if (totalLockers - activeCount <= 0)
                        return ApiResponse<CreateBookingResponse>.ErrorResponse("Không còn tủ trống cho kích thước này.");

                    // Generate booking
                    var bookingCode = "BK-" + DateTime.UtcNow.ToString("yyyyMMdd") + "-" + new Random().Next(1000, 9999);
                    var amount = finalAmount;
                    var expiresAt = DateTime.UtcNow.AddMinutes(10);

                    if (voucherToUpdate != null)
                    {
                        voucherToUpdate.UsedCount += 1;
                        voucherToUpdate.UpdatedAt = DateTime.UtcNow;
                    }

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
                        OriginalAmount = originalAmount,
                        DiscountAmount = discountAmount,
                        VoucherCode = appliedVoucherCode,
                        Amount = amount,
                        PaymentUrl = paymentResult.CheckoutUrl,
                        PaymentExpiresAt = expiresAt,
                        QrCode = paymentResult.QrCode,
                        AccountNumber = paymentResult.AccountNumber,
                        AccountName = paymentResult.AccountName,
                        Bin = paymentResult.Bin,
                        OrderCode = orderCode
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
        finally
        {
            stationLock.Release();
        }
    }

    /// <summary>
    /// UC-T05 / UC-T14: Xem chi tiết booking
    /// Tự động đối soát trực tiếp với PayOS nếu có giao dịch PENDING (cả BASE, EXTENSION, OVERDUE)
    /// </summary>
    public async Task<ApiResponse<BookingDto>> GetBookingByIdAsync(Guid userId, Guid bookingId)
    {
        var booking = await _context.Bookings
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.PricingPolicy)
            .Include(b => b.Payments)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null) return ApiResponse<BookingDto>.ErrorResponse("Không tìm thấy đặt tủ.");
        if (booking.UserId != userId) return ApiResponse<BookingDto>.ErrorResponse("Không có quyền truy cập.");

        // Tự động đối soát với PayOS cho mọi giao dịch đang PENDING (đặt mới BASE, gia hạn EXTENSION, hoặc phí quá hạn OVERDUE)
        var pendingPayments = booking.Payments
            .Where(p => p.Status == PaymentStatus.PENDING)
            .OrderByDescending(p => p.CreatedAt)
            .ToList();

        var syncedAny = false;
        foreach (var pendingPayment in pendingPayments)
        {
            if (long.TryParse(pendingPayment.OrderCode, out var orderCode))
            {
                var payosStatus = await _paymentGateway.GetPaymentStatusAsync(orderCode);
                _logger.LogInformation("GetBookingByIdAsync: Checking PayOS status for booking {BookingCode}, kind {Kind}, orderCode {OrderCode}. Status: {PayOSStatus}",
                    booking.BookingCode, pendingPayment.Kind, orderCode, payosStatus);

                if (string.Equals(payosStatus, "PAID", StringComparison.OrdinalIgnoreCase))
                {
                    await _paymentService.SyncPaymentStatusAsync(orderCode);
                    syncedAny = true;
                }
            }
        }

        if (syncedAny)
        {
            booking = await _context.Bookings
                .Include(b => b.Station)
                .Include(b => b.Locker)
                .Include(b => b.PricingPolicy)
                .Include(b => b.Payments)
                .Include(b => b.AccessCredential)
                .FirstOrDefaultAsync(b => b.Id == bookingId) ?? booking;
        }

        var policy = booking.PricingPolicy;
        if (policy == null && booking.PricingPolicyId != null)
        {
            policy = await _context.PricingPolicies.FindAsync(booking.PricingPolicyId);
        }
        policy ??= await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);

        // Real-time overdue detection for STORED bookings
        var graceMins = policy?.GracePeriodMinutes ?? 15;
        var overdueLimit = booking.EndAt.AddMinutes(graceMins);
        if (booking.Status == BookingStatus.STORED && !booking.IsOverdue && DateTime.UtcNow > overdueLimit)
        {
            booking.IsOverdue = true;
            booking.OverdueSince = overdueLimit;
            booking.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        var dto = MapToBookingDto(booking);

        if (policy != null)
        {
            dto.HourlyRate = policy.PricePerBlock;
            dto.MaxHours = policy.MaxHours;
            dto.OverdueFeePerHour = policy.OverdueFeePerHour;
            dto.GracePeriodMinutes = policy.GracePeriodMinutes;
            if (booking.IsOverdue)
            {
                var overdueMinutes = Math.Max(1, (DateTime.UtcNow - booking.EndAt).TotalMinutes - policy.GracePeriodMinutes);
                dto.OverdueHours = Math.Max(1, (int)Math.Ceiling(overdueMinutes / 60.0));
                dto.CurrentOverdueFee = _pricingService.CalculateOverdueFee(booking, policy);
                if ((dto.CurrentOverdueFee ?? 0) <= 0)
                {
                    dto.CurrentOverdueFee = dto.OverdueHours * policy.OverdueFeePerHour;
                }
            }
        }

        if (dto.CanCancel)
        {
            booking.PricingPolicy = policy;
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
            .Include(b => b.PricingPolicy)
            .Include(b => b.AccessCredential)
            .Include(b => b.Payments)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        bool anyOverdueUpdated = false;
        foreach (var b in entities)
        {
            var grace = b.PricingPolicy?.GracePeriodMinutes ?? 15;
            var limit = b.EndAt.AddMinutes(grace);
            if (b.Status == BookingStatus.STORED && !b.IsOverdue && DateTime.UtcNow > limit)
            {
                b.IsOverdue = true;
                b.OverdueSince = limit;
                b.UpdatedAt = DateTime.UtcNow;
                anyOverdueUpdated = true;
            }
        }
        if (anyOverdueUpdated)
        {
            await _context.SaveChangesAsync();
        }

        var items = entities.Select(b =>
        {
            var basePayment = b.Payments.FirstOrDefault(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PAID);
            var confirmedAt = basePayment?.PaidAt ?? b.CreatedAt;
            var holdExpiresAt = b.Status == BookingStatus.CONFIRMED ? (DateTime?)confirmedAt.AddMinutes(30) : null;
            var duration = Math.Max(1, Math.Round((b.EndAt - b.StartAt).TotalHours, 1));
            int? overdueHours = null;
            decimal? overdueFee = null;
            if (b.IsOverdue)
            {
                var grace = b.PricingPolicy?.GracePeriodMinutes ?? 15;
                var feePerHour = b.PricingPolicy?.OverdueFeePerHour ?? 20000m;
                var overdueMinutes = Math.Max(1, (DateTime.UtcNow - b.EndAt).TotalMinutes - grace);
                overdueHours = Math.Max(1, (int)Math.Ceiling(overdueMinutes / 60.0));
                overdueFee = b.PricingPolicy != null
                    ? _pricingService.CalculateOverdueFee(b, b.PricingPolicy)
                    : overdueHours * feePerHour;
                if ((overdueFee ?? 0) <= 0) overdueFee = overdueHours * feePerHour;
            }

            return new BookingListItemDto
            {
                Id = b.Id,
                BookingCode = b.BookingCode,
                StationId = b.StationId,
                StationName = b.Station.Name,
                StationAddress = b.Station.Address,
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
                HourlyRate = b.PricingPolicy?.PricePerBlock,
                MaxHours = b.PricingPolicy?.MaxHours,
                IsOverdue = b.IsOverdue,
                OverdueSince = b.OverdueSince,
                OverdueHours = overdueHours,
                OverdueFeePerHour = b.PricingPolicy?.OverdueFeePerHour,
                CurrentOverdueFee = overdueFee,
                CanExtend = b.Status == BookingStatus.STORED,
                CanCancel = b.Status == BookingStatus.PENDING_PAYMENT || b.Status == BookingStatus.CONFIRMED,
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
    /// UC-T14: Gia hạn booking → tạo payment gia hạn → trả thông tin VietQR & link PayOS
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

            var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
            if (policy == null) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không có chính sách giá.");

            var totalDuration = (request.NewEndAt - booking.StartAt).TotalHours;
            if (totalDuration > policy.MaxHours)
                return ApiResponse<CreateBookingResponse>.ErrorResponse($"Tổng thời gian lưu trữ không được vượt quá {policy.MaxHours} giờ.");

            var amount = await _pricingService.CalculateExtensionPriceAsync(booking, request.NewEndAt);
            if (booking.IsOverdue) amount += _pricingService.CalculateOverdueFee(booking, policy);

            // TC_BKO_20: Kiểm tra xem đã có giao dịch gia hạn PENDING nào cùng số tiền được tạo gần đây không
            var existingPendingPayment = await _context.Payments
                .Where(p => p.BookingId == booking.Id
                         && p.Kind == PaymentKind.EXTENSION
                         && p.Status == PaymentStatus.PENDING)
                .OrderByDescending(p => p.CreatedAt)
                .FirstOrDefaultAsync();

            if (existingPendingPayment != null)
            {
                if (existingPendingPayment.CreatedAt >= DateTime.UtcNow.AddMinutes(-10)
                    && existingPendingPayment.Amount == amount
                    && !string.IsNullOrEmpty(existingPendingPayment.PaymentLink)
                    && long.TryParse(existingPendingPayment.OrderCode, out var existingCode))
                {
                    _logger.LogInformation("TC_BKO_20: Reusing existing pending extension payment for booking {BookingId}, orderCode {OrderCode}",
                        booking.Id, existingPendingPayment.OrderCode);
                    return ApiResponse<CreateBookingResponse>.SuccessResponse(new CreateBookingResponse
                    {
                        BookingId = booking.Id,
                        BookingCode = booking.BookingCode,
                        Amount = existingPendingPayment.Amount,
                        PaymentUrl = existingPendingPayment.PaymentLink,
                        PaymentExpiresAt = existingPendingPayment.ExpiresAt ?? DateTime.UtcNow.AddMinutes(10),
                        OrderCode = existingCode
                    }, "Đã có yêu cầu thanh toán gia hạn đang chờ xử lý.");
                }
                else
                {
                    existingPendingPayment.Status = PaymentStatus.EXPIRED;
                    await _context.SaveChangesAsync();
                }
            }

            var expiresAt = DateTime.UtcNow.AddMinutes(10);
            var orderCode = long.Parse(DateTime.UtcNow.ToString("yyMMddHHmm") + new Random().Next(1000, 9999).ToString());
            var payment = new Payment
            {
                BookingId = booking.Id,
                Kind = PaymentKind.EXTENSION,
                Status = PaymentStatus.PENDING,
                Amount = amount,
                OrderCode = orderCode.ToString(),
                ExpiresAt = expiresAt,
                Gateway = "PAYOS",
                WebhookPayload = $"{{\"NewEndAt\":\"{request.NewEndAt:O}\"}}"
            };
            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();

            var paymentResult = await _paymentGateway.CreatePaymentLinkAsync(
                orderCode, (int)amount, $"Gia han {booking.BookingCode}", "", "");

            payment.PaymentLink = paymentResult.CheckoutUrl;
            payment.GatewayTxnId = paymentResult.PaymentLinkId;
            await _context.SaveChangesAsync();

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
                Bin = paymentResult.Bin,
                OrderCode = orderCode
            }, "Tạo mã VietQR thanh toán gia hạn thành công.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// UC-T20: Xem trước thông tin hủy đơn & chính sách hoàn tiền (áp dụng giới hạn 3 lần/ngày chống phá web)
    /// </summary>
    public async Task<ApiResponse<CancelPreviewDto>> GetCancelPreviewAsync(Guid userId, Guid bookingId)
    {
        var booking = await _context.Bookings
            .Include(b => b.Payments)
            .Include(b => b.PricingPolicy)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null) return ApiResponse<CancelPreviewDto>.ErrorResponse("Không tìm thấy đơn đặt tủ.");
        if (booking.UserId != userId) return ApiResponse<CancelPreviewDto>.ErrorResponse("Bạn không có quyền thao tác trên đơn này.");

        var canCancel = booking.Status == BookingStatus.PENDING_PAYMENT || booking.Status == BookingStatus.CONFIRMED;
        string? reasonIfBlocked = null;
        if (!canCancel)
        {
            if (booking.Status == BookingStatus.STORED)
                reasonIfBlocked = "Hành lý đã được gửi vào tủ (STORED). Bạn không thể hủy đơn, vui lòng mở tủ lấy đồ và hoàn tất trả tủ.";
            else if (booking.Status == BookingStatus.COMPLETED || booking.Status == BookingStatus.COMPLETED_MANUAL)
                reasonIfBlocked = "Đơn đặt tủ đã hoàn tất trước đó.";
            else if (booking.Status == BookingStatus.CANCELLED)
                reasonIfBlocked = "Đơn đặt tủ đã bị hủy.";
            else
                reasonIfBlocked = $"Không thể hủy đơn ở trạng thái {booking.Status}.";
        }

        var basePayment = booking.Payments.FirstOrDefault(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PAID);
        var actualPaidAmount = basePayment?.Amount ?? 0m;

        // Nếu đơn chưa thanh toán
        if (booking.Status == BookingStatus.PENDING_PAYMENT || actualPaidAmount <= 0)
        {
            return ApiResponse<CancelPreviewDto>.SuccessResponse(new CancelPreviewDto
            {
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                Status = booking.Status.ToString(),
                CanCancel = canCancel,
                PaidAmount = 0,
                RefundRate = 0,
                RefundAmount = 0,
                DailyCancellationCount = 0,
                IsDailyLimitExceeded = false,
                PolicyTier = "UNPAID",
                PolicyDescription = "Đơn chưa thanh toán. Hủy đơn tức thì và không phát sinh bất kỳ khoản phí nào.",
                ReasonIfBlocked = reasonIfBlocked
            });
        }

        // Đếm số lần user đã hủy đơn có tiền hoàn trong ngày hôm nay (UTC date hoặc GMT+7)
        var todayStart = DateTime.UtcNow.Date;
        var dailyCancelledWithRefundCount = await _context.Bookings
            .Where(b => b.UserId == userId 
                     && b.Status == BookingStatus.CANCELLED 
                     && b.UpdatedAt >= todayStart
                     && b.CancelPolicyRate > 0)
            .CountAsync();

        decimal refundRate;
        decimal refundAmount;
        string policyTier;
        string policyDesc;
        bool isDailyLimitExceeded = dailyCancelledWithRefundCount >= 2; // Lần này sẽ là lần thứ 3 trở lên

        if (isDailyLimitExceeded)
        {
            refundRate = 0m;
            refundAmount = 0m;
            policyTier = "DAILY_LIMIT_EXCEEDED";
            policyDesc = $"Bạn đã thực hiện {dailyCancelledWithRefundCount} lần hủy hoàn tiền trong ngày hôm nay. Theo quy định chống lạm dụng hệ thống, từ lần hủy thứ 3 trong ngày sẽ thu 100% phí giữ chỗ (không hoàn tiền).";
        }
        else
        {
            refundRate = 1.00m;
            refundAmount = actualPaidAmount;
            policyTier = "FULL";
            policyDesc = $"Đơn đủ điều kiện hoàn 100% số tiền thực trả ({actualPaidAmount:N0} VND). Phiếu yêu cầu hoàn tiền sẽ được gửi tới Ban quản trị (Admin) phê duyệt.";
        }

        return ApiResponse<CancelPreviewDto>.SuccessResponse(new CancelPreviewDto
        {
            BookingId = booking.Id,
            BookingCode = booking.BookingCode,
            Status = booking.Status.ToString(),
            CanCancel = canCancel,
            PaidAmount = actualPaidAmount,
            RefundRate = refundRate,
            RefundAmount = refundAmount,
            DailyCancellationCount = dailyCancelledWithRefundCount,
            IsDailyLimitExceeded = isDailyLimitExceeded,
            PolicyTier = policyTier,
            PolicyDescription = policyDesc,
            ReasonIfBlocked = reasonIfBlocked
        });
    }

    /// <summary>
    /// UC-T20: Hủy booking + tạo phiếu yêu cầu hoàn tiền cho Admin duyệt (áp dụng giới hạn 3 lần/ngày chống phá web)
    /// </summary>
    public async Task<ApiResponse<BookingDto>> CancelBookingAsync(Guid userId, Guid bookingId, CancelBookingRequest request)
    {
        var semaphore = _bookingLocks.GetOrAdd(bookingId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            var booking = await _context.Bookings
                .Include(b => b.PricingPolicy)
                .Include(b => b.Payments)
                .Include(b => b.Locker)
                .Include(b => b.AccessCredential)
                .Include(b => b.Station)
                .FirstOrDefaultAsync(b => b.Id == bookingId);

            if (booking == null) return ApiResponse<BookingDto>.ErrorResponse("Không tìm thấy đặt tủ.");
            if (booking.UserId != userId) return ApiResponse<BookingDto>.ErrorResponse("Bạn không có quyền thao tác trên đơn này.");

            // Idempotency: nếu đã CANCELLED rồi thì trả về luôn
            if (booking.Status == BookingStatus.CANCELLED)
                return await GetBookingByIdAsync(userId, bookingId);

            if (booking.Status != BookingStatus.PENDING_PAYMENT && booking.Status != BookingStatus.CONFIRMED)
                return ApiResponse<BookingDto>.ErrorResponse("Không thể hủy đặt tủ ở trạng thái hiện tại. Chỉ hủy được khi chưa gửi đồ vào tủ (PENDING_PAYMENT hoặc CONFIRMED).");

            var basePayment = booking.Payments.FirstOrDefault(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PAID);
            var actualPaidAmount = basePayment?.Amount ?? 0m;
            var nowUtc = DateTime.UtcNow;

            decimal refundRate = 0m;
            decimal refundAmount = 0m;

            if (booking.Status == BookingStatus.CONFIRMED && actualPaidAmount > 0)
            {
                // Kiểm tra giới hạn hủy hoàn tiền trong ngày (chống phá web)
                var todayStart = nowUtc.Date;
                var dailyCancelledCount = await _context.Bookings
                    .Where(b => b.UserId == userId 
                             && b.Status == BookingStatus.CANCELLED 
                             && b.UpdatedAt >= todayStart
                             && b.CancelPolicyRate > 0)
                    .CountAsync();

                if (dailyCancelledCount >= 2)
                {
                    // Từ lần thứ 3 trở đi trong ngày: Thu hết phí, không hoàn tiền
                    refundRate = 0m;
                    refundAmount = 0m;
                    booking.CancelPolicyRate = 0m;
                }
                else
                {
                    // Lần 1 và 2: Hoàn 100% tiền thực trả
                    refundRate = 1.00m;
                    refundAmount = actualPaidAmount;
                    booking.CancelPolicyRate = 1.00m;
                }
            }
            else
            {
                booking.CancelPolicyRate = 0m;
            }

            booking.Status = BookingStatus.CANCELLED;
            booking.CancellationReason = string.IsNullOrWhiteSpace(request?.Reason) ? "Khách yêu cầu hủy giữ chỗ" : request.Reason.Trim();
            booking.UpdatedAt = nowUtc;

            // 1. Giải phóng ngăn tủ vật lý nếu đã được gán trước đó
            if (booking.Locker != null)
            {
                booking.Locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
                booking.Locker.UpdatedAt = nowUtc;
            }

            // 2. Thu hồi mã mở tủ PIN / QR
            if (booking.AccessCredential != null)
            {
                booking.AccessCredential.IsActive = false;
                booking.AccessCredential.RevokedAt = nowUtc;
                booking.AccessCredential.RevokeReason = "Booking cancelled by customer (UC-T20)";
            }

            // 3. Tạo phiếu yêu cầu hoàn tiền PENDING để Admin duyệt
            if (refundAmount > 0)
            {
                // Kiểm tra xem đã có payment REFUND nào cho booking này chưa để tránh duplicate
                var existingRefund = await _context.Payments
                    .FirstOrDefaultAsync(p => p.BookingId == booking.Id && p.Kind == PaymentKind.REFUND);

                if (existingRefund == null)
                {
                    _context.Payments.Add(new Payment
                    {
                        BookingId = booking.Id,
                        Kind = PaymentKind.REFUND,
                        Status = PaymentStatus.PENDING, // Chờ Admin phê duyệt
                        Amount = refundAmount,
                        Currency = "VND",
                        OrderCode = "REF-" + Guid.NewGuid().ToString("N")[..12].ToUpper(),
                        Gateway = "PAYOS",
                        CreatedAt = nowUtc,
                        UpdatedAt = nowUtc
                    });
                }
            }

            // 4. Hủy các link thanh toán PayOS PENDING nếu khách chưa thanh toán
            var pendingPayments = booking.Payments.Where(p => p.Status == PaymentStatus.PENDING && p.Kind == PaymentKind.BASE).ToList();
            foreach (var p in pendingPayments)
            {
                p.Status = PaymentStatus.EXPIRED;
                p.UpdatedAt = nowUtc;
                if (long.TryParse(p.OrderCode, out var code))
                {
                    try
                    {
                        await _paymentGateway.CancelPaymentLinkAsync(code, "Booking cancelled");
                    }
                    catch
                    {
                        // Bỏ qua lỗi PayOS nếu link đã hết hạn hoặc không tồn tại
                    }
                }
            }

            await _context.SaveChangesAsync();
            _logger.LogInformation("Booking {Code} cancelled by User {UserId}. Refund amount: {Amount} (Status: PENDING Admin review)", 
                booking.BookingCode, userId, refundAmount);

            return await GetBookingByIdAsync(userId, bookingId);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// UC-T19: Thanh toán phụ phí quá hạn trực tiếp trên Web (trả về VietQR PayOS + OrderCode)
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

            var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
            if (policy == null) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không có chính sách giá.");

            // Kiểm tra quá hạn thời gian thực
            var overdueLimit = booking.EndAt.AddMinutes(policy.GracePeriodMinutes);
            if (booking.Status == BookingStatus.STORED && !booking.IsOverdue && DateTime.UtcNow > overdueLimit)
            {
                booking.IsOverdue = true;
                booking.OverdueSince = overdueLimit;
                await _context.SaveChangesAsync();
            }

            if (booking.Status != BookingStatus.STORED || !booking.IsOverdue)
                return ApiResponse<CreateBookingResponse>.ErrorResponse("Đặt tủ hiện không có phụ phí quá hạn.");

            var amount = _pricingService.CalculateOverdueFee(booking, policy);
            if (amount <= 0)
            {
                var overdueHours = Math.Max(1, (int)Math.Ceiling(Math.Max(1, (DateTime.UtcNow - booking.EndAt).TotalMinutes - policy.GracePeriodMinutes) / 60.0));
                amount = overdueHours * policy.OverdueFeePerHour;
            }
            if (amount < 2000m) amount = 2000m;

            // Hủy các giao dịch OVERDUE PENDING cũ để tạo mã VietQR mới nhất
            var oldPendings = await _context.Payments
                .Where(p => p.BookingId == booking.Id && p.Kind == PaymentKind.OVERDUE && p.Status == PaymentStatus.PENDING)
                .ToListAsync();
            foreach (var old in oldPendings)
            {
                old.Status = PaymentStatus.EXPIRED;
            }

            var expiresAt = DateTime.UtcNow.AddMinutes(10);
            var orderCode = long.Parse(DateTime.UtcNow.ToString("yyMMddHHmm") + new Random().Next(1000, 9999).ToString());
            var payment = new Payment
            {
                BookingId = booking.Id,
                Kind = PaymentKind.OVERDUE,
                Status = PaymentStatus.PENDING,
                Amount = amount,
                OrderCode = orderCode.ToString(),
                ExpiresAt = expiresAt,
                Gateway = "PAYOS"
            };
            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();

            var paymentResult = await _paymentGateway.CreatePaymentLinkAsync(
                orderCode, (int)amount, $"Qua han {booking.BookingCode}", "", "");

            payment.PaymentLink = paymentResult.CheckoutUrl;
            payment.GatewayTxnId = paymentResult.PaymentLinkId;
            await _context.SaveChangesAsync();

            return ApiResponse<CreateBookingResponse>.SuccessResponse(new CreateBookingResponse
            {
                BookingId = booking.Id,
                BookingCode = booking.BookingCode,
                OriginalAmount = amount,
                DiscountAmount = 0,
                Amount = amount,
                PaymentUrl = paymentResult.CheckoutUrl,
                PaymentExpiresAt = expiresAt,
                QrCode = paymentResult.QrCode,
                AccountNumber = paymentResult.AccountNumber,
                AccountName = paymentResult.AccountName,
                Bin = paymentResult.Bin,
                OrderCode = orderCode
            }, "Tạo mã VietQR thanh toán phụ phí quá hạn thành công.");
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// UC-T19 Helper: Giả lập trạng thái quá hạn cho đơn đang STORED hoặc CONFIRMED để kiểm thử nhanh
    /// </summary>
    public async Task<ApiResponse<BookingDto>> SimulateOverdueAsync(Guid userId, Guid bookingId, int overdueHours = 2)
    {
        var booking = await _context.Bookings
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.PricingPolicy)
            .Include(b => b.Payments)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
            return ApiResponse<BookingDto>.ErrorResponse("Không tìm thấy đơn đặt tủ.");
        if (booking.UserId != userId)
            return ApiResponse<BookingDto>.ErrorResponse("Không có quyền thao tác trên đơn này.");

        var now = DateTime.UtcNow;
        var hoursOverdue = Math.Max(1, overdueHours);

        if (booking.Status == BookingStatus.CONFIRMED)
        {
            booking.Status = BookingStatus.STORED;
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
                }
            }
        }

        if (booking.Status != BookingStatus.STORED)
            return ApiResponse<BookingDto>.ErrorResponse("Chỉ có thể giả lập quá hạn cho đơn đang lưu trữ (STORED) hoặc đã xác nhận (CONFIRMED).");

        var graceMinutes = booking.PricingPolicy?.GracePeriodMinutes ?? 15;
        booking.CheckedInAt = now.AddHours(-(hoursOverdue + 2));
        booking.StartAt = booking.CheckedInAt.Value;
        // Đặt EndAt lùi về quá khứ sao cho vượt quá GracePeriod đúng hoursOverdue giờ
        booking.EndAt = now.AddHours(-hoursOverdue).AddMinutes(-(graceMinutes + 5));
        booking.IsOverdue = true;
        booking.OverdueSince = booking.EndAt.AddMinutes(graceMinutes);
        booking.UpdatedAt = now;

        await _context.SaveChangesAsync();
        return await GetBookingByIdAsync(userId, bookingId);
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
            .Include(b => b.PricingPolicy)
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
        var policy = booking.PricingPolicy ?? await _pricingService.GetActivePolicyAsync(booking.StationId, booking.Size);
        var graceMins = policy?.GracePeriodMinutes ?? 15;
        var overdueLimit = booking.EndAt.AddMinutes(graceMins);

        if (!booking.IsOverdue && now > overdueLimit)
        {
            booking.IsOverdue = true;
            booking.OverdueSince = overdueLimit;
            booking.UpdatedAt = now;
            await _context.SaveChangesAsync();
        }

        if (booking.IsOverdue)
        {
            return ApiResponse<BookingDto>.ErrorResponse("Đơn đặt tủ đang quá hạn lưu trữ. Vui lòng thanh toán phụ phí quá hạn trước khi mở tủ lấy đồ (UC-T19).");
        }

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
