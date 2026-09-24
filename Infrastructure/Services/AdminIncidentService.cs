namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class AdminIncidentService : IAdminIncidentService
{
    private readonly ISmartLockerDbContext _db;
    private readonly IAuditLogService _auditLog;
    private readonly ILogger<AdminIncidentService> _logger;

    public AdminIncidentService(ISmartLockerDbContext db, IAuditLogService auditLog, ILogger<AdminIncidentService> logger)
    {
        _db = db;
        _auditLog = auditLog;
        _logger = logger;
    }

    public async Task<ApiResponse<List<IncidentDto>>> GetIncidentsAsync(string? status, string? severity)
    {
        var query = _db.SecurityIncidents.AsNoTracking()
            .Include(i => i.Locker)
            .Include(i => i.Station)
            .Include(i => i.ReportedByUser)
            .Include(i => i.ResolvedByUser)
            .Include(i => i.Booking)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<IncidentStatus>(status, true, out var statusEnum))
        {
            query = query.Where(i => i.Status == statusEnum);
        }

        if (!string.IsNullOrWhiteSpace(severity))
        {
            query = query.Where(i => i.Severity == severity);
        }

        var incidents = await query
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new IncidentDto
            {
                Id = i.Id,
                IncidentCode = i.IncidentCode,
                BookingId = i.BookingId,
                BookingCode = i.Booking != null ? i.Booking.BookingCode : null,
                LockerId = i.LockerId,
                LockerCode = i.Locker.LockerCode,
                StationId = i.StationId,
                StationName = i.Station.Name,
                ReportedBy = i.ReportedByUser.FullName,
                Type = i.Type.ToString(),
                Status = i.Status.ToString(),
                Severity = i.Severity,
                Description = i.Description,
                ResolutionNote = i.ResolutionNote,
                ResolvedBy = i.ResolvedByUser != null ? i.ResolvedByUser.FullName : null,
                ResolvedAt = i.ResolvedAt,
                RemoteUnlocked = i.RemoteUnlocked,
                CreatedAt = i.CreatedAt
            })
            .ToListAsync();

        return ApiResponse<List<IncidentDto>>.SuccessResponse(incidents);
    }

    public async Task<ApiResponse<IncidentDto>> CreateIncidentAsync(CreateIncidentRequest request, Guid reportedByUserId)
    {
        var locker = await _db.Lockers.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.LockerId);
        if (locker == null)
            return ApiResponse<IncidentDto>.ErrorResponse("Không tìm thấy ô tủ");

        var station = await _db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.StationId);
        if (station == null)
            return ApiResponse<IncidentDto>.ErrorResponse("Không tìm thấy trạm");

        var reporter = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == reportedByUserId);

        var incident = new SecurityIncident
        {
            Id = Guid.NewGuid(),
            IncidentCode = $"INC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpper()}",
            BookingId = request.BookingId,
            LockerId = request.LockerId,
            StationId = request.StationId,
            ReportedByUserId = reportedByUserId,
            Type = request.Type,
            Status = IncidentStatus.OPEN,
            Severity = request.Severity,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow
        };

        _db.SecurityIncidents.Add(incident);
        await _db.SaveChangesAsync();

        _logger.LogWarning("🚨 [INCIDENT] Sự cố mới {IncidentCode}: {Type} tại trạm {StationName}, tủ {LockerCode}",
            incident.IncidentCode, incident.Type, station.Name, locker.LockerCode);

        var dto = new IncidentDto
        {
            Id = incident.Id,
            IncidentCode = incident.IncidentCode,
            BookingId = incident.BookingId,
            LockerId = incident.LockerId,
            LockerCode = locker.LockerCode,
            StationId = incident.StationId,
            StationName = station.Name,
            ReportedBy = reporter?.FullName ?? "Unknown",
            Type = incident.Type.ToString(),
            Status = incident.Status.ToString(),
            Severity = incident.Severity,
            Description = incident.Description,
            RemoteUnlocked = false,
            CreatedAt = incident.CreatedAt
        };

        return ApiResponse<IncidentDto>.SuccessResponse(dto, "Đã tạo báo cáo sự cố thành công");
    }

    public async Task<ApiResponse<bool>> RemoteUnlockEmergencyAsync(Guid incidentId, RemoteUnlockRequest request, Guid adminOrStaffUserId, string? userEmail, string? userRole)
    {
        var incident = await _db.SecurityIncidents
            .Include(i => i.Locker)
            .FirstOrDefaultAsync(i => i.Id == incidentId);

        if (incident == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy sự cố");

        if (incident.Status == IncidentStatus.RESOLVED)
            return ApiResponse<bool>.ErrorResponse("Sự cố này đã được xử lý trước đó");

        // === Thực hiện mở khóa tủ từ xa ===
        // Trong production, đây sẽ gọi API đến IoT Gateway để gửi lệnh mở cửa
        // Hiện tại giả lập bằng cách cập nhật trạng thái DoorState
        if (incident.Locker != null)
        {
            incident.Locker.DoorState = "OPEN";
            incident.Locker.LastDoorEventAt = DateTime.UtcNow;
        }

        incident.RemoteUnlocked = true;
        incident.Status = IncidentStatus.RESOLVED;
        incident.ResolvedByUserId = adminOrStaffUserId;
        incident.ResolvedAt = DateTime.UtcNow;
        incident.ResolutionNote = $"Mở khóa từ xa (Emergency). Lý do: {request.Reason}";

        await _db.SaveChangesAsync();

        _logger.LogWarning("🔓 [REMOTE UNLOCK] Đã mở tủ từ xa cho sự cố {IncidentCode}. Bởi: {UserEmail}. Lý do: {Reason}",
            incident.IncidentCode, userEmail, request.Reason);

        // Ghi nhận audit log
        await _auditLog.LogAsync(
            adminOrStaffUserId,
            userEmail,
            userRole,
            "REMOTE_EMERGENCY_UNLOCK",
            "Locker",
            incident.LockerId.ToString(),
            $"Mở tủ khẩn cấp từ xa cho sự cố {incident.IncidentCode}. Lý do: {request.Reason}"
        );

        return ApiResponse<bool>.SuccessResponse(true, $"Đã mở khóa tủ từ xa thành công cho sự cố {incident.IncidentCode}");
    }

    public async Task<ApiResponse<bool>> DirectRemoteUnlockLockerAsync(Guid lockerId, RemoteUnlockRequest request, Guid adminOrStaffUserId, string? userEmail, string? userRole)
    {
        var locker = await _db.Lockers.FirstOrDefaultAsync(l => l.Id == lockerId);
        if (locker == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy ô tủ");

        // Tự động tạo Incident record cho trường hợp mở tủ trực tiếp
        var incident = new SecurityIncident
        {
            Id = Guid.NewGuid(),
            IncidentCode = $"INC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpper()}",
            LockerId = lockerId,
            StationId = locker.StationId,
            ReportedByUserId = adminOrStaffUserId,
            Type = IncidentType.LOCK_FAILURE,
            Status = IncidentStatus.RESOLVED,
            Severity = "HIGH",
            Description = $"Mở tủ từ xa trực tiếp: {request.Reason}",
            RemoteUnlocked = true,
            ResolvedByUserId = adminOrStaffUserId,
            ResolvedAt = DateTime.UtcNow,
            ResolutionNote = $"Mở khóa từ xa trực tiếp. Lý do: {request.Reason}",
            CreatedAt = DateTime.UtcNow
        };

        _db.SecurityIncidents.Add(incident);

        // Giả lập mở cửa tủ
        locker.DoorState = "OPEN";
        locker.LastDoorEventAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogWarning("🔓 [DIRECT REMOTE UNLOCK] Đã mở tủ {LockerCode} từ xa. Bởi: {UserEmail}. Lý do: {Reason}",
            locker.LockerCode, userEmail, request.Reason);

        await _auditLog.LogAsync(
            adminOrStaffUserId,
            userEmail,
            userRole,
            "DIRECT_REMOTE_UNLOCK",
            "Locker",
            lockerId.ToString(),
            $"Mở tủ {locker.LockerCode} từ xa trực tiếp. Lý do: {request.Reason}"
        );

        return ApiResponse<bool>.SuccessResponse(true, $"Đã mở khóa tủ {locker.LockerCode} từ xa thành công");
    }
}
