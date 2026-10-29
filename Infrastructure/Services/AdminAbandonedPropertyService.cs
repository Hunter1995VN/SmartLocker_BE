namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

public class AdminAbandonedPropertyService : IAdminAbandonedPropertyService
{
    private readonly ISmartLockerDbContext _db;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<AdminAbandonedPropertyService> _logger;

    public AdminAbandonedPropertyService(ISmartLockerDbContext db, IAuditLogService auditLog, ILogger<AdminAbandonedPropertyService> logger)
    {
        _db = db;
        _auditLog = auditLog;
        _logger = logger;
    }

    public async Task<ApiResponse<List<AbandonedPropertyRecordDto>>> GetAbandonedPropertiesAsync(string? status)
    {
        var query = _db.AbandonedPropertyRecords.AsNoTracking()
            .Include(a => a.Booking)
                .ThenInclude(b => b.User)
            .Include(a => a.Locker)
            .Include(a => a.Station)
            .Include(a => a.StaffWitness)
            .Include(a => a.AdminApproval)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<AbandonedPropertyStatus>(status, true, out var statusEnum))
        {
            query = query.Where(a => a.Status == statusEnum);
        }

        var records = await query
            .OrderByDescending(a => a.ReportedAt)
            .Select(a => new AbandonedPropertyRecordDto
            {
                Id = a.Id,
                RecordCode = a.RecordCode,
                BookingId = a.BookingId,
                BookingCode = a.Booking.BookingCode,
                LockerId = a.LockerId,
                LockerCode = a.Locker.LockerCode,
                StationId = a.StationId,
                StationName = a.Station.Name,
                CustomerName = a.Booking.User != null ? a.Booking.User.FullName : "Khách vãng lai",
                CustomerPhone = a.Booking.User != null ? a.Booking.User.Phone : "",
                CustomerEmail = a.Booking.User != null ? a.Booking.User.Email : "",
                OverdueDebt = a.Booking.User != null ? a.Booking.User.OverdueDebt : 0,
                OverdueHours = a.OverdueHours,
                ItemDescription = a.ItemDescription,
                StaffWitnessName = a.StaffWitness.FullName,
                AdminApprovalName = a.AdminApproval != null ? a.AdminApproval.FullName : null,
                Status = a.Status.ToString(),
                DisposalAction = a.DisposalAction,
                Notes = a.Notes,
                ReportedAt = a.ReportedAt,
                ApprovedAt = a.ApprovedAt,
                ResolvedAt = a.ResolvedAt
            })
            .ToListAsync();

        var photosLookup = await query.ToDictionaryAsync(a => a.Id, a => a.InventoryPhotosJson);
        foreach (var r in records)
        {
            if (photosLookup.TryGetValue(r.Id, out var json) && json != null)
            {
                try
                {
                    r.Photos = JsonSerializer.Deserialize<List<string>>(json);
                }
                catch
                {
                    r.Photos = new List<string>();
                }
            }
        }

        return ApiResponse<List<AbandonedPropertyRecordDto>>.SuccessResponse(records);
    }

    /// <summary>
    /// Lấy danh sách các đơn đặt tủ đang lưu trữ / quá hạn để Staff chọn lập biên bản niêm phong 72h (UC-S10)
    /// </summary>
    public async Task<ApiResponse<List<OverdueBookingCandidateDto>>> GetOverdueCandidatesAsync()
    {
        var now = DateTime.UtcNow;
        var bookings = await _db.Bookings.AsNoTracking()
            .Include(b => b.User)
            .Include(b => b.Locker)
            .Include(b => b.Station)
            .Where(b => b.LockerId != null && (b.Status == BookingStatus.STORED || b.Status == BookingStatus.CONFIRMED || b.IsOverdue))
            .OrderByDescending(b => b.IsOverdue)
            .ThenBy(b => b.EndAt)
            .Take(50)
            .ToListAsync();

        var candidates = bookings.Select(b =>
        {
            var diffHours = b.EndAt < now ? (int)Math.Max(72, Math.Ceiling((now - b.EndAt).TotalHours)) : 72;
            return new OverdueBookingCandidateDto
            {
                BookingId = b.Id,
                BookingCode = b.BookingCode,
                LockerId = b.LockerId ?? Guid.Empty,
                LockerCode = b.Locker?.LockerCode ?? "S-01",
                StationId = b.StationId,
                StationName = b.Station?.Name ?? "",
                CustomerName = b.User?.FullName ?? "Khách hàng",
                CustomerPhone = b.User?.Phone ?? "",
                CustomerEmail = b.User?.Email ?? "",
                OverdueDebt = b.User?.OverdueDebt ?? 0,
                OverdueHours = diffHours,
                EndAt = b.EndAt
            };
        }).ToList();

        return ApiResponse<List<OverdueBookingCandidateDto>>.SuccessResponse(candidates);
    }

    /// <summary>
    /// Mắt 1: Staff lập biên bản niêm phong đồ quá hạn 72h kèm ảnh/video kiểm kê (UC-S10)
    /// </summary>
    public async Task<ApiResponse<AbandonedPropertyRecordDto>> CreateRecordAsync(CreateAbandonedPropertyRequest request, Guid staffWitnessId)
    {
        var booking = await _db.Bookings
            .Include(b => b.User)
            .FirstOrDefaultAsync(b => b.Id == request.BookingId);
        if (booking == null)
            return ApiResponse<AbandonedPropertyRecordDto>.ErrorResponse("Không tìm thấy đơn đặt tủ");

        var locker = await _db.Lockers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.LockerId);
        if (locker == null)
            return ApiResponse<AbandonedPropertyRecordDto>.ErrorResponse("Không tìm thấy ô tủ");

        var station = await _db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StationId);
        if (station == null)
            return ApiResponse<AbandonedPropertyRecordDto>.ErrorResponse("Không tìm thấy trạm");

        var staff = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == staffWitnessId);

        var overdueHours = request.OverdueHours > 0 ? request.OverdueHours : 72;

        var record = new AbandonedPropertyRecord
        {
            Id = Guid.NewGuid(),
            RecordCode = $"ABP-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpper()}",
            BookingId = request.BookingId,
            LockerId = request.LockerId,
            StationId = request.StationId,
            OverdueHours = overdueHours,
            ItemDescription = request.ItemDescription,
            InventoryPhotosJson = request.PhotoUrls != null && request.PhotoUrls.Count > 0
                ? JsonSerializer.Serialize(request.PhotoUrls)
                : null,
            StaffWitnessId = staffWitnessId,
            Status = AbandonedPropertyStatus.PENDING_APPROVAL,
            Notes = request.Notes,
            ReportedAt = DateTime.UtcNow
        };

        // Đánh dấu đơn hàng sang trạng thái quá hạn / chờ xử lý tài sản bỏ quên
        booking.IsOverdue = true;
        if (booking.OverdueSince == null)
        {
            booking.OverdueSince = DateTime.UtcNow.AddHours(-overdueHours);
        }

        _db.AbandonedPropertyRecords.Add(record);
        await _db.SaveChangesAsync();

        _logger.LogInformation("📦 [ABANDONED PROPERTY] Staff {StaffName} lập biên bản 72h {RecordCode} cho tủ {LockerCode} tại trạm {StationName}",
            staff?.FullName, record.RecordCode, locker.LockerCode, station.Name);

        await _auditLog.LogAsync(staffWitnessId, staff?.Email, "STAFF", "CREATE_ABANDONED_RECORD", "AbandonedPropertyRecord", record.Id.ToString(),
            $"Staff lập biên bản niêm phong 72h {record.RecordCode} (Quá hạn {overdueHours}h, Số ảnh/video: {request.PhotoUrls?.Count ?? 0}): {request.ItemDescription}");

        var dto = new AbandonedPropertyRecordDto
        {
            Id = record.Id,
            RecordCode = record.RecordCode,
            BookingId = record.BookingId,
            BookingCode = booking.BookingCode,
            LockerId = record.LockerId,
            LockerCode = locker.LockerCode,
            StationId = record.StationId,
            StationName = station.Name,
            CustomerName = booking.User?.FullName ?? "Khách hàng",
            CustomerPhone = booking.User?.Phone ?? "",
            CustomerEmail = booking.User?.Email ?? "",
            OverdueDebt = booking.User?.OverdueDebt ?? 0,
            OverdueHours = record.OverdueHours,
            ItemDescription = record.ItemDescription,
            Photos = request.PhotoUrls,
            StaffWitnessName = staff?.FullName ?? "",
            Status = record.Status.ToString(),
            Notes = record.Notes,
            ReportedAt = record.ReportedAt
        };

        return ApiResponse<AbandonedPropertyRecordDto>.SuccessResponse(dto, "Đã lập biên bản niêm phong 72h thành công. Chờ Admin phê duyệt (Quy tắc 4 mắt).");
    }

    /// <summary>
    /// Mắt 2: Admin phê duyệt biên bản thanh lý đồ bỏ quên sau 72h (Quy tắc 4 mắt - UC-A07)
    /// </summary>
    public async Task<ApiResponse<bool>> ApproveRecordAsync(Guid recordId, ApproveAbandonedPropertyRequest request, Guid adminApprovalId)
    {
        var record = await _db.AbandonedPropertyRecords
            .Include(a => a.Locker)
            .FirstOrDefaultAsync(a => a.Id == recordId);

        if (record == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy biên bản kiểm kê");

        if (record.Status != AbandonedPropertyStatus.PENDING_APPROVAL && record.Status != AbandonedPropertyStatus.REPORTED)
            return ApiResponse<bool>.ErrorResponse($"Biên bản đang ở trạng thái {record.Status}, không thể phê duyệt");

        // Kiểm tra quyền: phải là ADMIN (nguyên tắc 4 mắt: Staff lập, Admin duyệt)
        var admin = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == adminApprovalId);
        if (admin == null || admin.Role != UserRole.ADMIN)
            return ApiResponse<bool>.ErrorResponse("Chỉ Admin mới có quyền phê duyệt biên bản kiểm kê (Nguyên tắc 4 mắt)");

        // Ngăn self-approval (Staff không tự duyệt biên bản của mình)
        if (record.StaffWitnessId == adminApprovalId)
            return ApiResponse<bool>.ErrorResponse("Không được phép tự phê duyệt biên bản do chính mình lập (Nguyên tắc 4 mắt)");

        record.AdminApprovalId = adminApprovalId;
        record.Status = AbandonedPropertyStatus.APPROVED;
        record.DisposalAction = request.Action;
        record.ApprovedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.ApprovalNotes))
        {
            record.Notes = (record.Notes ?? "") + $"\n[Admin 4-Eyes Approval] {request.ApprovalNotes}";
        }

        // Mở tủ sau khi duyệt để lấy đồ ra lưu kho / bàn giao
        if (record.Locker != null)
        {
            record.Locker.DoorState = "OPEN";
            record.Locker.LastDoorEventAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("✅ [ABANDONED PROPERTY APPROVED] Admin {AdminName} duyệt biên bản {RecordCode}. Hành động: {Action}",
            admin.FullName, record.RecordCode, request.Action);

        await _auditLog.LogAsync(adminApprovalId, admin.Email, "ADMIN", "APPROVE_ABANDONED_RECORD", "AbandonedPropertyRecord", record.Id.ToString(),
            $"Admin duyệt biên bản 4 mắt {record.RecordCode}. Hành động xử lý: {request.Action}. Ghi chú: {request.ApprovalNotes}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã phê duyệt biên bản {record.RecordCode}. Hành động: {request.Action}");
    }

    /// <summary>
    /// Admin từ chối / trả lại biên bản để Staff kiểm kê lại (UC-A07)
    /// </summary>
    public async Task<ApiResponse<bool>> RejectRecordAsync(Guid recordId, RejectAbandonedPropertyRequest request, Guid adminApprovalId)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse<bool>.ErrorResponse("Vui lòng nhập lý do từ chối phê duyệt");

        var record = await _db.AbandonedPropertyRecords.FirstOrDefaultAsync(a => a.Id == recordId);
        if (record == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy biên bản kiểm kê");

        var admin = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == adminApprovalId);
        if (admin == null || admin.Role != UserRole.ADMIN)
            return ApiResponse<bool>.ErrorResponse("Chỉ Admin mới có quyền từ chối biên bản kiểm kê");

        record.Status = AbandonedPropertyStatus.REPORTED;
        record.Notes = (record.Notes ?? "") + $"\n[Admin Từ chối duyệt - {DateTime.UtcNow:dd/MM/yyyy HH:mm}] Lý do: {request.Reason.Trim()}";

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(adminApprovalId, admin.Email, "ADMIN", "REJECT_ABANDONED_RECORD", "AbandonedPropertyRecord", record.Id.ToString(),
            $"Admin từ chối biên bản {record.RecordCode}. Lý do: {request.Reason.Trim()}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã từ chối biên bản {record.RecordCode} và chuyển trả để bổ sung bằng chứng");
    }

    /// <summary>
    /// Hoàn tất xử lý cơ bản (backwards-compatible)
    /// </summary>
    public Task<ApiResponse<bool>> ResolveRecordAsync(Guid recordId, string disposalAction, string? notes, Guid staffOrAdminId)
    {
        return ResolveRecordDetailAsync(recordId, new ResolveAbandonedPropertyDetailRequest
        {
            DisposalAction = disposalAction,
            Notes = notes
        }, staffOrAdminId);
    }

    /// <summary>
    /// UC-S11 & UC-A07: Thủ tục bàn giao lại đồ thất lạc cho khách đến nhận diện (RETURNED_TO_OWNER) hoặc hoàn tất thanh lý (LIQUIDATED)
    /// </summary>
    public async Task<ApiResponse<bool>> ResolveRecordDetailAsync(Guid recordId, ResolveAbandonedPropertyDetailRequest request, Guid staffOrAdminId)
    {
        var record = await _db.AbandonedPropertyRecords
            .Include(a => a.Locker)
            .Include(a => a.Booking)
                .ThenInclude(b => b.User)
            .FirstOrDefaultAsync(a => a.Id == recordId);

        if (record == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy biên bản kiểm kê");

        if (record.Status != AbandonedPropertyStatus.APPROVED && record.Status != AbandonedPropertyStatus.PENDING_APPROVAL)
            return ApiResponse<bool>.ErrorResponse($"Biên bản đang ở trạng thái {record.Status}, không thể thực hiện bàn giao/hoàn tất.");

        var actor = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == staffOrAdminId);
        var isReturnToOwner = string.Equals(request.DisposalAction, "RETURNED_TO_OWNER", StringComparison.OrdinalIgnoreCase);

        record.Status = isReturnToOwner
            ? AbandonedPropertyStatus.RETURNED
            : AbandonedPropertyStatus.DISPOSED;
        record.DisposalAction = request.DisposalAction;
        record.ResolvedAt = DateTime.UtcNow;

        var summaryParts = new List<string>();
        if (isReturnToOwner)
        {
            if (!string.IsNullOrWhiteSpace(request.ReceiverName))
                summaryParts.Add($"Người nhận: {request.ReceiverName}");
            if (!string.IsNullOrWhiteSpace(request.ReceiverIdNumber))
                summaryParts.Add($"CCCD/Passport: {request.ReceiverIdNumber}");
            if (!string.IsNullOrWhiteSpace(request.HandoverProofUrl))
                summaryParts.Add($"Ảnh biên bản: {request.HandoverProofUrl}");
        }
        if (!string.IsNullOrWhiteSpace(request.Notes))
            summaryParts.Add(request.Notes.Trim());

        var resolveNote = string.Join(" | ", summaryParts);
        if (!string.IsNullOrWhiteSpace(resolveNote))
        {
            record.Notes = (record.Notes ?? "") + $"\n[{(isReturnToOwner ? "Bàn giao khách UC-S11" : "Hoàn tất thanh lý")}] {resolveNote}";
        }

        // Cập nhật Booking và công nợ khách hàng khi bàn giao lại đồ (UC-S11)
        if (record.Booking != null)
        {
            record.Booking.Status = isReturnToOwner ? BookingStatus.COMPLETED_MANUAL : BookingStatus.ABANDONED;
            record.Booking.IsOverdue = false;
            record.Booking.CheckedOutAt = DateTime.UtcNow;

            if (isReturnToOwner && request.CollectOverdueDebt && record.Booking.User != null && record.Booking.User.OverdueDebt > 0)
            {
                record.Booking.User.OverdueDebt = 0;
                if (record.Booking.User.Status == UserStatus.SUSPENDED)
                {
                    record.Booking.User.Status = UserStatus.ACTIVE;
                }
            }
        }

        // Giải phóng tủ sau khi đã lấy đồ ra
        if (record.Locker != null)
        {
            record.Locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
            record.Locker.HealthStatus = LockerHealthStatus.HEALTHY;
            record.Locker.DoorState = "CLOSED";
            record.Locker.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        var auditAction = isReturnToOwner ? "HANDOVER_ABANDONED_PROPERTY" : "RESOLVE_ABANDONED_RECORD";
        await _auditLog.LogAsync(
            staffOrAdminId,
            actor?.Email,
            actor?.Role.ToString(),
            auditAction,
            "AbandonedPropertyRecord",
            record.Id.ToString(),
            $"Hoàn tất biên bản {record.RecordCode} ({request.DisposalAction}). {resolveNote}"
        );

        return ApiResponse<bool>.SuccessResponse(
            true,
            isReturnToOwner
                ? $"Đã hoàn tất thủ tục bàn giao lại đồ thất lạc cho khách hàng (Biên bản {record.RecordCode})"
                : $"Đã hoàn tất xử lý thanh lý tài sản bỏ quên (Biên bản {record.RecordCode})");
    }
}
