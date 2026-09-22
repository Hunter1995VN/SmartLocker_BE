namespace Infrastructure.Services;

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
        var items = await query.OrderByDescending(b => b.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new BookingListItemDto
            {
                Id = b.Id,
                BookingCode = b.BookingCode,
                StationName = b.Station.Name,
                Size = b.Size.ToString(),
                Status = b.Status.ToString(),
                StartAt = b.StartAt,
                EndAt = b.EndAt,
                BaseAmount = b.BaseAmount,
                IsOverdue = b.IsOverdue,
                CreatedAt = b.CreatedAt
            })
            .ToListAsync();

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
    /// </summary>
    public async Task<ApiResponse<CreateBookingResponse>> ExtendBookingAsync(Guid userId, Guid bookingId, ExtendBookingRequest request)
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
    /// </summary>
    public async Task<ApiResponse<CreateBookingResponse>> PayOverdueFeeAsync(Guid userId, Guid bookingId)
    {
        var booking = await _context.Bookings.Include(b => b.PricingPolicy).FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking == null) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không tìm thấy đặt tủ.");
        if (booking.UserId != userId) return ApiResponse<CreateBookingResponse>.ErrorResponse("Không có quyền.");
        if (booking.Status != BookingStatus.STORED || !booking.IsOverdue)
            return ApiResponse<CreateBookingResponse>.ErrorResponse("Đặt tủ không có phí quá hạn.");

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

    // === Private helpers ===

    private static BookingDto MapToBookingDto(Booking booking) => new()
    {
        Id = booking.Id,
        BookingCode = booking.BookingCode,
        UserId = booking.UserId,
        StationId = booking.StationId,
        StationName = booking.Station.Name,
        StationAddress = booking.Station.Address,
        LockerCode = booking.Locker?.LockerCode,
        Size = booking.Size.ToString(),
        Status = booking.Status.ToString(),
        StartAt = booking.StartAt,
        EndAt = booking.EndAt,
        CheckedInAt = booking.CheckedInAt,
        CheckedOutAt = booking.CheckedOutAt,
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
