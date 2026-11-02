namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class AdminRefundService : IAdminRefundService
{
    private readonly ISmartLockerDbContext _context;
    private readonly ILogger<AdminRefundService> _logger;

    public AdminRefundService(ISmartLockerDbContext context, ILogger<AdminRefundService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ApiResponse<PagedResult<AdminRefundDto>>> GetRefundRequestsAsync(string? status, int page = 1, int pageSize = 20)
    {
        var query = _context.Payments
            .Include(p => p.Booking)
                .ThenInclude(b => b.User)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Station)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Locker)
            .Where(p => p.Kind == PaymentKind.REFUND);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<PaymentStatus>(status, true, out var filterStatus))
            {
                query = query.Where(p => p.Status == filterStatus);
            }
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new AdminRefundDto
            {
                PaymentId = p.Id,
                BookingId = p.BookingId,
                BookingCode = p.Booking.BookingCode,
                UserId = p.Booking.UserId,
                UserFullName = p.Booking.User.FullName,
                UserEmail = p.Booking.User.Email,
                UserPhoneNumber = p.Booking.User.Phone,
                StationName = p.Booking.Station.Name,
                LockerCode = p.Booking.Locker != null ? p.Booking.Locker.LockerCode : $"Size {p.Booking.Size}",
                OriginalAmount = p.Booking.BaseAmount,
                RefundAmount = p.Amount,
                Status = p.Status.ToString(),
                OrderCode = p.OrderCode,
                CancellationReason = p.Booking.CancellationReason,
                CancelPolicyRate = p.Booking.CancelPolicyRate,
                RequestedAt = p.CreatedAt,
                ProcessedAt = p.PaidAt
            })
            .ToListAsync();

        return ApiResponse<PagedResult<AdminRefundDto>>.SuccessResponse(new PagedResult<AdminRefundDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        });
    }

    public async Task<ApiResponse<AdminRefundDto>> ApproveRefundAsync(Guid paymentId, Guid adminId, ProcessRefundRequest? request)
    {
        var payment = await _context.Payments
            .Include(p => p.Booking)
                .ThenInclude(b => b.User)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Station)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Locker)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.Kind == PaymentKind.REFUND);

        if (payment == null)
            return ApiResponse<AdminRefundDto>.ErrorResponse("Không tìm thấy phiếu yêu cầu hoàn tiền.");

        if (payment.Status != PaymentStatus.PENDING)
            return ApiResponse<AdminRefundDto>.ErrorResponse($"Phiếu hoàn tiền đang ở trạng thái {payment.Status}, không thể phê duyệt lại.");

        var nowUtc = DateTime.UtcNow;
        payment.Status = PaymentStatus.PAID;
        payment.PaidAt = nowUtc;
        payment.UpdatedAt = nowUtc;

        // Cập nhật RefundAmount trên Payment BASE gốc nếu có
        var basePayment = await _context.Payments
            .FirstOrDefaultAsync(p => p.BookingId == payment.BookingId && p.Kind == PaymentKind.BASE);
        if (basePayment != null)
        {
            basePayment.RefundAmount = payment.Amount;
            basePayment.RefundedAt = nowUtc;
            basePayment.UpdatedAt = nowUtc;
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Admin {AdminId} approved refund {PaymentId} ({Amount} VND) for Booking {BookingCode}",
            adminId, payment.Id, payment.Amount, payment.Booking.BookingCode);

        return ApiResponse<AdminRefundDto>.SuccessResponse(new AdminRefundDto
        {
            PaymentId = payment.Id,
            BookingId = payment.BookingId,
            BookingCode = payment.Booking.BookingCode,
            UserId = payment.Booking.UserId,
            UserFullName = payment.Booking.User.FullName,
            UserEmail = payment.Booking.User.Email,
            UserPhoneNumber = payment.Booking.User.Phone,
            StationName = payment.Booking.Station.Name,
            LockerCode = payment.Booking.Locker != null ? payment.Booking.Locker.LockerCode : $"Size {payment.Booking.Size}",
            OriginalAmount = payment.Booking.BaseAmount,
            RefundAmount = payment.Amount,
            Status = payment.Status.ToString(),
            OrderCode = payment.OrderCode,
            CancellationReason = payment.Booking.CancellationReason,
            CancelPolicyRate = payment.Booking.CancelPolicyRate,
            RequestedAt = payment.CreatedAt,
            ProcessedAt = payment.PaidAt
        }, "Phê duyệt hoàn tiền thành công.");
    }

    public async Task<ApiResponse<AdminRefundDto>> RejectRefundAsync(Guid paymentId, Guid adminId, ProcessRefundRequest? request)
    {
        var payment = await _context.Payments
            .Include(p => p.Booking)
                .ThenInclude(b => b.User)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Station)
            .Include(p => p.Booking)
                .ThenInclude(b => b.Locker)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.Kind == PaymentKind.REFUND);

        if (payment == null)
            return ApiResponse<AdminRefundDto>.ErrorResponse("Không tìm thấy phiếu yêu cầu hoàn tiền.");

        if (payment.Status != PaymentStatus.PENDING)
            return ApiResponse<AdminRefundDto>.ErrorResponse($"Phiếu hoàn tiền đang ở trạng thái {payment.Status}, không thể từ chối.");

        var nowUtc = DateTime.UtcNow;
        payment.Status = PaymentStatus.FAILED;
        payment.UpdatedAt = nowUtc;

        await _context.SaveChangesAsync();
        _logger.LogInformation("Admin {AdminId} rejected refund {PaymentId} for Booking {BookingCode}. Reason: {Note}",
            adminId, payment.Id, payment.Booking.BookingCode, request?.Note);

        return ApiResponse<AdminRefundDto>.SuccessResponse(new AdminRefundDto
        {
            PaymentId = payment.Id,
            BookingId = payment.BookingId,
            BookingCode = payment.Booking.BookingCode,
            UserId = payment.Booking.UserId,
            UserFullName = payment.Booking.User.FullName,
            UserEmail = payment.Booking.User.Email,
            UserPhoneNumber = payment.Booking.User.Phone,
            StationName = payment.Booking.Station.Name,
            LockerCode = payment.Booking.Locker != null ? payment.Booking.Locker.LockerCode : $"Size {payment.Booking.Size}",
            OriginalAmount = payment.Booking.BaseAmount,
            RefundAmount = payment.Amount,
            Status = payment.Status.ToString(),
            OrderCode = payment.OrderCode,
            CancellationReason = payment.Booking.CancellationReason,
            CancelPolicyRate = payment.Booking.CancelPolicyRate,
            RequestedAt = payment.CreatedAt,
            ProcessedAt = null
        }, "Đã từ chối hoàn tiền.");
    }
}
