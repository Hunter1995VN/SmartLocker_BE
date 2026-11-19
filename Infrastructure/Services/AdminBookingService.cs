namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

public class AdminBookingService : IAdminBookingService
{
    private readonly ISmartLockerDbContext _db;
    private readonly IAuditLogService _auditLog;

    public AdminBookingService(ISmartLockerDbContext db, IAuditLogService auditLog)
    {
        _db = db;
        _auditLog = auditLog;
    }

    public async Task<ApiResponse<PagedResult<AdminBookingListItemDto>>> GetBookingsAsync(
        string? search,
        string? status,
        Guid? stationId,
        bool? isOverdue,
        DateTime? fromDate,
        DateTime? toDate,
        int page = 1,
        int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Bookings
            .AsNoTracking()
            .Include(b => b.User)
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.Payments)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(b =>
                b.BookingCode.ToLower().Contains(term) ||
                b.User.FullName.ToLower().Contains(term) ||
                b.User.Email.ToLower().Contains(term) ||
                (b.User.Phone != null && b.User.Phone.ToLower().Contains(term)) ||
                (b.Locker != null && b.Locker.LockerCode.ToLower().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<BookingStatus>(status.Trim(), true, out var parsedStatus))
        {
            query = query.Where(b => b.Status == parsedStatus);
        }

        if (stationId.HasValue && stationId.Value != Guid.Empty)
        {
            query = query.Where(b => b.StationId == stationId.Value);
        }

        if (isOverdue.HasValue)
        {
            query = query.Where(b => b.IsOverdue == isOverdue.Value);
        }

        if (fromDate.HasValue)
        {
            var fromUtc = DateTime.SpecifyKind(fromDate.Value.Date, DateTimeKind.Utc);
            query = query.Where(b => b.CreatedAt >= fromUtc);
        }

        if (toDate.HasValue)
        {
            var toUtc = DateTime.SpecifyKind(toDate.Value.Date.AddDays(1), DateTimeKind.Utc);
            query = query.Where(b => b.CreatedAt < toUtc);
        }

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new AdminBookingListItemDto
            {
                Id = b.Id,
                BookingCode = b.BookingCode,
                Status = b.Status.ToString(),
                Size = b.Size.ToString(),
                BaseAmount = b.BaseAmount,
                PaidAmount = b.Payments
                    .Where(p => p.Status == PaymentStatus.PAID)
                    .Sum(p => p.Amount),
                IsOverdue = b.IsOverdue,
                OverdueSince = b.OverdueSince,
                ExtensionCount = b.ExtensionCount,
                StartAt = b.StartAt,
                EndAt = b.EndAt,
                CheckedInAt = b.CheckedInAt,
                CheckedOutAt = b.CheckedOutAt,
                CreatedAt = b.CreatedAt,
                UserId = b.UserId,
                CustomerName = b.User.FullName,
                CustomerEmail = b.User.Email,
                CustomerPhone = b.User.Phone,
                StationId = b.StationId,
                StationName = b.Station.Name,
                LockerId = b.LockerId,
                LockerCode = b.Locker != null ? b.Locker.LockerCode : null
            })
            .ToListAsync();

        return ApiResponse<PagedResult<AdminBookingListItemDto>>.SuccessResponse(new PagedResult<AdminBookingListItemDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    public async Task<ApiResponse<AdminBookingStatsDto>> GetBookingStatsAsync(Guid? stationId)
    {
        var query = _db.Bookings.AsNoTracking().AsQueryable();
        if (stationId.HasValue && stationId.Value != Guid.Empty)
        {
            query = query.Where(b => b.StationId == stationId.Value);
        }

        var list = await query
            .Select(b => new
            {
                b.Status,
                b.IsOverdue,
                PaidSum = b.Payments.Where(p => p.Status == PaymentStatus.PAID).Sum(p => p.Amount)
            })
            .ToListAsync();

        var stats = new AdminBookingStatsDto
        {
            TotalBookings = list.Count,
            PendingPaymentCount = list.Count(x => x.Status == BookingStatus.PENDING_PAYMENT),
            ConfirmedCount = list.Count(x => x.Status == BookingStatus.CONFIRMED),
            StoredCount = list.Count(x => x.Status == BookingStatus.STORED),
            CompletedCount = list.Count(x => x.Status == BookingStatus.COMPLETED || x.Status == BookingStatus.COMPLETED_MANUAL),
            CancelledCount = list.Count(x => x.Status == BookingStatus.CANCELLED || x.Status == BookingStatus.EXPIRED || x.Status == BookingStatus.NO_SHOW),
            OverdueCount = list.Count(x => x.IsOverdue && (x.Status == BookingStatus.STORED || x.Status == BookingStatus.CONFIRMED)),
            TotalRevenue = list.Sum(x => x.PaidSum)
        };

        return ApiResponse<AdminBookingStatsDto>.SuccessResponse(stats);
    }

    public async Task<ApiResponse<AdminBookingDetailDto>> GetBookingDetailAsync(Guid bookingId)
    {
        var booking = await _db.Bookings
            .AsNoTracking()
            .Include(b => b.User)
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.Payments)
            .Include(b => b.Extensions)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
        {
            return ApiResponse<AdminBookingDetailDto>.ErrorResponse("Không tìm thấy đơn đặt tủ.");
        }

        return ApiResponse<AdminBookingDetailDto>.SuccessResponse(MapToDetailDto(booking));
    }

    public async Task<ApiResponse<AdminBookingDetailDto>> CancelBookingByAdminAsync(
        Guid adminUserId,
        Guid bookingId,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return ApiResponse<AdminBookingDetailDto>.ErrorResponse("Vui lòng nhập lý do hủy đơn.");
        }

        var booking = await _db.Bookings
            .Include(b => b.User)
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.Payments)
            .Include(b => b.Extensions)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
        {
            return ApiResponse<AdminBookingDetailDto>.ErrorResponse("Không tìm thấy đơn đặt tủ.");
        }

        if (booking.Status != BookingStatus.PENDING_PAYMENT && booking.Status != BookingStatus.CONFIRMED)
        {
            return ApiResponse<AdminBookingDetailDto>.ErrorResponse(
                $"Không thể hủy đơn ở trạng thái '{booking.Status}'. Nếu khách đang gửi đồ, vui lòng dùng tính năng Hoàn tất thủ công.");
        }

        var now = DateTime.UtcNow;
        booking.Status = BookingStatus.CANCELLED;
        booking.CancellationReason = $"[Admin hủy] {reason.Trim()}";
        booking.UpdatedAt = now;

        if (booking.Locker != null && booking.Locker.BusinessStatus == LockerBusinessStatus.RESERVED)
        {
            booking.Locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
            booking.Locker.UpdatedAt = now;
        }

        if (booking.AccessCredential != null && booking.AccessCredential.IsActive)
        {
            booking.AccessCredential.IsActive = false;
            booking.AccessCredential.RevokedAt = now;
            booking.AccessCredential.RevokeReason = "Admin cancelled booking";
        }

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(
            adminUserId,
            null,
            "ADMIN",
            "ADMIN_CANCEL_BOOKING",
            "Booking",
            booking.Id.ToString(),
            $"Hủy đơn đặt tủ '{booking.BookingCode}' (Khách: {booking.User.FullName}). Lý do: {reason.Trim()}");

        return ApiResponse<AdminBookingDetailDto>.SuccessResponse(MapToDetailDto(booking), "Đã hủy đơn đặt tủ thành công.");
    }

    public async Task<ApiResponse<AdminBookingDetailDto>> ForceCompleteBookingByAdminAsync(
        Guid adminUserId,
        Guid bookingId,
        string? note)
    {
        var booking = await _db.Bookings
            .Include(b => b.User)
            .Include(b => b.Station)
            .Include(b => b.Locker)
            .Include(b => b.Payments)
            .Include(b => b.Extensions)
            .Include(b => b.AccessCredential)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking == null)
        {
            return ApiResponse<AdminBookingDetailDto>.ErrorResponse("Không tìm thấy đơn đặt tủ.");
        }

        if (booking.Status == BookingStatus.COMPLETED ||
            booking.Status == BookingStatus.COMPLETED_MANUAL ||
            booking.Status == BookingStatus.CANCELLED)
        {
            return ApiResponse<AdminBookingDetailDto>.ErrorResponse($"Đơn hàng đã ở trạng thái kết thúc ({booking.Status}).");
        }

        var now = DateTime.UtcNow;
        booking.Status = BookingStatus.COMPLETED_MANUAL;
        booking.CheckedOutAt ??= now;
        booking.IsOverdue = false;
        booking.UpdatedAt = now;
        if (!string.IsNullOrWhiteSpace(note))
        {
            booking.CancellationReason = $"[Hoàn tất thủ công] {note.Trim()}";
        }

        if (booking.Locker != null &&
            (booking.Locker.BusinessStatus == LockerBusinessStatus.RESERVED ||
             booking.Locker.BusinessStatus == LockerBusinessStatus.OCCUPIED))
        {
            booking.Locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
            booking.Locker.UpdatedAt = now;
        }

        if (booking.AccessCredential != null && booking.AccessCredential.IsActive)
        {
            booking.AccessCredential.IsActive = false;
            booking.AccessCredential.RevokedAt = now;
            booking.AccessCredential.RevokeReason = "Admin force completed booking";
        }

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(
            adminUserId,
            null,
            "ADMIN",
            "ADMIN_COMPLETE_BOOKING",
            "Booking",
            booking.Id.ToString(),
            $"Hoàn tất thủ công đơn '{booking.BookingCode}' (Trạm: {booking.Station.Name}). Ghi chú: {note?.Trim() ?? "Không có"}");

        return ApiResponse<AdminBookingDetailDto>.SuccessResponse(MapToDetailDto(booking), "Đã hoàn tất đơn hàng và giải phóng ô tủ thành công.");
    }

    private static AdminBookingDetailDto MapToDetailDto(Domain.Entities.Booking b)
    {
        return new AdminBookingDetailDto
        {
            Id = b.Id,
            BookingCode = b.BookingCode,
            Status = b.Status.ToString(),
            Size = b.Size.ToString(),
            BaseAmount = b.BaseAmount,
            PaidAmount = b.Payments.Where(p => p.Status == PaymentStatus.PAID).Sum(p => p.Amount),
            IsOverdue = b.IsOverdue,
            OverdueSince = b.OverdueSince,
            ExtensionCount = b.ExtensionCount,
            StartAt = b.StartAt,
            EndAt = b.EndAt,
            CheckedInAt = b.CheckedInAt,
            CheckedOutAt = b.CheckedOutAt,
            CreatedAt = b.CreatedAt,
            UserId = b.UserId,
            CustomerName = b.User.FullName,
            CustomerEmail = b.User.Email,
            CustomerPhone = b.User.Phone,
            StationId = b.StationId,
            StationName = b.Station.Name,
            LockerId = b.LockerId,
            LockerCode = b.Locker?.LockerCode,
            CancellationReason = b.CancellationReason,
            AccessCode = b.AccessCredential?.ProtectedAccessCode,
            Payments = b.Payments
                .OrderByDescending(p => p.CreatedAt)
                .Select(p => new AdminBookingPaymentDto
                {
                    Id = p.Id,
                    Kind = p.Kind.ToString(),
                    Status = p.Status.ToString(),
                    Amount = p.Amount,
                    OrderCode = p.OrderCode,
                    Gateway = p.Gateway,
                    PaidAt = p.PaidAt,
                    CreatedAt = p.CreatedAt
                })
                .ToList(),
            Extensions = b.Extensions
                .OrderByDescending(e => e.ExtendedAt)
                .Select(e => new AdminBookingExtensionDto
                {
                    Id = e.Id,
                    OldEndAt = e.OldEndAt,
                    NewEndAt = e.NewEndAt,
                    ExtendedAt = e.ExtendedAt
                })
                .ToList()
        };
    }
}
