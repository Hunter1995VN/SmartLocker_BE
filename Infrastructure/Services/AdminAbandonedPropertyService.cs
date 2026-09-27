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

        // Deserialize photos JSON client-side (can't be done inside EF expression tree)
        var photosLookup = await query.ToDictionaryAsync(a => a.Id, a => a.InventoryPhotosJson);
        foreach (var r in records)
        {
            if (photosLookup.TryGetValue(r.Id, out var json) && json != null)
            {
                r.Photos = JsonSerializer.Deserialize<List<string>>(json);
            }
        }

        return ApiResponse<List<AbandonedPropertyRecordDto>>.SuccessResponse(records);
    }

    /// <summary>
    /// Mắt 1: Staff lập biên bản kiểm kê tài sản bỏ quên
    /// </summary>
    public async Task<ApiResponse<AbandonedPropertyRecordDto>> CreateRecordAsync(CreateAbandonedPropertyRequest request, Guid staffWitnessId)
    {
        var booking = await _db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BookingId);
        if (booking == null)
            return ApiResponse<AbandonedPropertyRecordDto>.ErrorResponse("Không tìm thấy đơn đặt tủ");

        var locker = await _db.Lockers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.LockerId);
        if (locker == null)
            return ApiResponse<AbandonedPropertyRecordDto>.ErrorResponse("Không tìm thấy ô tủ");

        var station = await _db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StationId);
        if (station == null)
            return ApiResponse<AbandonedPropertyRecordDto>.ErrorResponse("Không tìm thấy trạm");

        var staff = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == staffWitnessId);

        var record = new AbandonedPropertyRecord
        {
            Id = Guid.NewGuid(),
            RecordCode = $"ABP-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpper()}",
            BookingId = request.BookingId,
            LockerId = request.LockerId,
            StationId = request.StationId,
            OverdueHours = request.OverdueHours,
            ItemDescription = request.ItemDescription,
            InventoryPhotosJson = request.PhotoUrls != null ? JsonSerializer.Serialize(request.PhotoUrls) : null,
            StaffWitnessId = staffWitnessId,
            Status = AbandonedPropertyStatus.PENDING_APPROVAL,
            Notes = request.Notes,
            ReportedAt = DateTime.UtcNow
        };

        _db.AbandonedPropertyRecords.Add(record);
        await _db.SaveChangesAsync();

        _logger.LogInformation("📦 [ABANDONED PROPERTY] Staff {StaffName} lập biên bản {RecordCode} cho tủ {LockerCode} tại trạm {StationName}",
            staff?.FullName, record.RecordCode, locker.LockerCode, station.Name);

        await _auditLog.LogAsync(staffWitnessId, null, "STAFF", "CREATE_ABANDONED_RECORD", "AbandonedPropertyRecord", record.Id.ToString(),
            $"Staff lập biên bản kiểm kê {record.RecordCode}: {request.ItemDescription}");

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
            OverdueHours = record.OverdueHours,
            ItemDescription = record.ItemDescription,
            Photos = request.PhotoUrls,
            StaffWitnessName = staff?.FullName ?? "",
            Status = record.Status.ToString(),
            Notes = record.Notes,
            ReportedAt = record.ReportedAt
        };

        return ApiResponse<AbandonedPropertyRecordDto>.SuccessResponse(dto, "Đã lập biên bản kiểm kê thành công. Chờ Admin phê duyệt.");
    }

    /// <summary>
    /// Mắt 2: Admin phê duyệt biên bản và quyết định hành động xử lý
    /// </summary>
    public async Task<ApiResponse<bool>> ApproveRecordAsync(Guid recordId, ApproveAbandonedPropertyRequest request, Guid adminApprovalId)
    {
        var record = await _db.AbandonedPropertyRecords
            .Include(a => a.Locker)
            .FirstOrDefaultAsync(a => a.Id == recordId);

        if (record == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy biên bản kiểm kê");

        if (record.Status != AbandonedPropertyStatus.PENDING_APPROVAL)
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
            record.Notes = (record.Notes ?? "") + $"\n[Admin] {request.ApprovalNotes}";
        }

        // Mở tủ sau khi duyệt để lấy đồ ra
        if (record.Locker != null)
        {
            record.Locker.DoorState = "OPEN";
            record.Locker.LastDoorEventAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("✅ [ABANDONED PROPERTY APPROVED] Admin {AdminName} duyệt biên bản {RecordCode}. Hành động: {Action}",
            admin.FullName, record.RecordCode, request.Action);

        await _auditLog.LogAsync(adminApprovalId, admin.Email, "ADMIN", "APPROVE_ABANDONED_RECORD", "AbandonedPropertyRecord", record.Id.ToString(),
            $"Admin duyệt biên bản {record.RecordCode}. Hành động xử lý: {request.Action}. Ghi chú: {request.ApprovalNotes}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã phê duyệt biên bản {record.RecordCode}. Hành động: {request.Action}");
    }

    /// <summary>
    /// Hoàn tất xử lý (sau khi đã duyệt): Staff thực hiện lưu kho / thanh lý / trả khách
    /// </summary>
    public async Task<ApiResponse<bool>> ResolveRecordAsync(Guid recordId, string disposalAction, string? notes, Guid staffOrAdminId)
    {
        var record = await _db.AbandonedPropertyRecords
            .Include(a => a.Locker)
            .FirstOrDefaultAsync(a => a.Id == recordId);

        if (record == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy biên bản kiểm kê");

        if (record.Status != AbandonedPropertyStatus.APPROVED)
            return ApiResponse<bool>.ErrorResponse($"Biên bản phải ở trạng thái APPROVED mới có thể hoàn tất. Hiện tại: {record.Status}");

        record.Status = disposalAction == "RETURNED_TO_OWNER"
            ? AbandonedPropertyStatus.RETURNED
            : AbandonedPropertyStatus.DISPOSED;
        record.DisposalAction = disposalAction;
        record.ResolvedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(notes))
        {
            record.Notes = (record.Notes ?? "") + $"\n[Resolve] {notes}";
        }

        // Giải phóng tủ sau khi đã lấy đồ ra
        if (record.Locker != null)
        {
            record.Locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
            record.Locker.DoorState = "CLOSED";
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("🗂️ [ABANDONED PROPERTY RESOLVED] Biên bản {RecordCode} đã hoàn tất. Hành động: {Action}",
            record.RecordCode, disposalAction);

        await _auditLog.LogAsync(staffOrAdminId, null, null, "RESOLVE_ABANDONED_RECORD", "AbandonedPropertyRecord", record.Id.ToString(),
            $"Hoàn tất xử lý biên bản {record.RecordCode}. Hành động: {disposalAction}. Ghi chú: {notes}");

        return ApiResponse<bool>.SuccessResponse(true, $"Đã hoàn tất xử lý biên bản {record.RecordCode}");
    }
}
