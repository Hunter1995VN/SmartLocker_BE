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
    private readonly IMqttPublishService _mqttPublish;
    private readonly ILogger<AdminIncidentService> _logger;

    public AdminIncidentService(
        ISmartLockerDbContext db,
        IAuditLogService auditLog,
        IMqttPublishService mqttPublish,
        ILogger<AdminIncidentService> logger)
    {
        _db = db;
        _auditLog = auditLog;
        _mqttPublish = mqttPublish;
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

        // Đảm bảo Type tương thích với SQL Server CHECK constraint chk_Incidents_Type
        var safeType = request.Type;
        var description = request.Description;
        if (safeType == IncidentType.WRONG_LOCKER || safeType == IncidentType.LOST_ITEMS)
        {
            description = $"[{safeType}] {description}";
            safeType = IncidentType.OTHER;
        }

        var incident = new SecurityIncident
        {
            Id = Guid.NewGuid(),
            IncidentCode = $"INC-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpper()}",
            BookingId = request.BookingId,
            LockerId = request.LockerId,
            StationId = request.StationId,
            ReportedByUserId = reportedByUserId,
            Type = safeType,
            Status = IncidentStatus.OPEN,
            Severity = string.IsNullOrWhiteSpace(request.Severity) ? "MEDIUM" : request.Severity.ToUpperInvariant(),
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        _db.SecurityIncidents.Add(incident);
        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(
            reportedByUserId,
            reporter?.Email,
            reporter?.Role.ToString(),
            "CREATE_INCIDENT",
            "SecurityIncident",
            incident.Id.ToString(),
            $"Tạo báo cáo sự cố {incident.IncidentCode} ({incident.Type} - {incident.Severity}) tại tủ {locker.LockerCode}, trạm {station.Name}: {incident.Description}"
        );

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

    public async Task<ApiResponse<IncidentDto>> UpdateIncidentStatusAsync(Guid incidentId, UpdateIncidentStatusRequest request, Guid adminOrStaffUserId, string? userEmail, string? userRole)
    {
        var incident = await _db.SecurityIncidents
            .Include(i => i.Locker)
            .Include(i => i.Station)
            .Include(i => i.ReportedByUser)
            .Include(i => i.Booking)
            .FirstOrDefaultAsync(i => i.Id == incidentId);

        if (incident == null)
            return ApiResponse<IncidentDto>.ErrorResponse("Không tìm thấy sự cố");

        if (!Enum.TryParse<IncidentStatus>(request.Status, true, out var newStatus))
            return ApiResponse<IncidentDto>.ErrorResponse($"Trạng thái '{request.Status}' không hợp lệ");

        var oldStatus = incident.Status;
        incident.Status = newStatus;

        if (!string.IsNullOrWhiteSpace(request.ResolutionNote))
        {
            incident.ResolutionNote = request.ResolutionNote.Trim();
        }

        var resolver = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == adminOrStaffUserId);

        if (newStatus == IncidentStatus.RESOLVED || newStatus == IncidentStatus.CANCELLED)
        {
            incident.ResolvedByUserId = adminOrStaffUserId;
            incident.ResolvedAt = DateTime.UtcNow;

            if (newStatus == IncidentStatus.RESOLVED && incident.Locker != null && incident.Locker.HealthStatus != LockerHealthStatus.MAINTENANCE)
            {
                incident.Locker.HealthStatus = LockerHealthStatus.HEALTHY;
                incident.Locker.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();

        await _auditLog.LogAsync(
            adminOrStaffUserId,
            userEmail ?? resolver?.Email,
            userRole ?? resolver?.Role.ToString(),
            "UPDATE_INCIDENT_STATUS",
            "SecurityIncident",
            incident.Id.ToString(),
            $"Cập nhật sự cố {incident.IncidentCode} từ {oldStatus} sang {newStatus}. Ghi chú xử lý: {incident.ResolutionNote}"
        );

        var dto = new IncidentDto
        {
            Id = incident.Id,
            IncidentCode = incident.IncidentCode,
            BookingId = incident.BookingId,
            BookingCode = incident.Booking?.BookingCode,
            LockerId = incident.LockerId,
            LockerCode = incident.Locker?.LockerCode ?? "",
            StationId = incident.StationId,
            StationName = incident.Station?.Name ?? "",
            ReportedBy = incident.ReportedByUser?.FullName ?? "Unknown",
            Type = incident.Type.ToString(),
            Status = incident.Status.ToString(),
            Severity = incident.Severity,
            Description = incident.Description,
            ResolutionNote = incident.ResolutionNote,
            ResolvedBy = resolver?.FullName,
            ResolvedAt = incident.ResolvedAt,
            RemoteUnlocked = incident.RemoteUnlocked,
            CreatedAt = incident.CreatedAt
        };

        return ApiResponse<IncidentDto>.SuccessResponse(dto, $"Đã cập nhật trạng thái sự cố {incident.IncidentCode} thành {newStatus}");
    }

    public async Task<ApiResponse<bool>> RemoteUnlockEmergencyAsync(Guid incidentId, RemoteUnlockRequest request, Guid adminOrStaffUserId, string? userEmail, string? userRole)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5)
            return ApiResponse<bool>.ErrorResponse("Bắt buộc nhập lý do mở tủ khẩn cấp (Audit Reason tối thiểu 5 ký tự) theo quy định an ninh UC-S05.");

        var incident = await _db.SecurityIncidents
            .Include(i => i.Locker)
            .FirstOrDefaultAsync(i => i.Id == incidentId);

        if (incident == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy sự cố");

        if (incident.Status == IncidentStatus.RESOLVED)
            return ApiResponse<bool>.ErrorResponse("Sự cố này đã được xử lý trước đó");

        // === Thực hiện mở khóa tủ từ xa qua MQTT + cập nhật DB ===
        if (incident.Locker != null)
        {
            string deviceCode = "ESP32-01";
            if (incident.Locker.IoTDeviceId.HasValue)
            {
                var iot = await _db.IoTDevices.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == incident.Locker.IoTDeviceId.Value);
                if (!string.IsNullOrWhiteSpace(iot?.DeviceCode))
                {
                    deviceCode = iot.DeviceCode;
                }
            }

            try
            {
                await _mqttPublish.PublishUnlockCommandAsync(
                    deviceCode,
                    incident.Locker.Id,
                    incident.Locker.LockerCode,
                    $"EMG-{incident.IncidentCode}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Không thể gửi lệnh MQTT cho thiết bị {DeviceCode}, tiếp tục mở khóa trạng thái DB", deviceCode);
            }

            incident.Locker.DoorState = "OPEN";
            incident.Locker.LastDoorEventAt = DateTime.UtcNow;
        }

        var cleanReason = request.Reason.Trim();
        incident.RemoteUnlocked = true;
        incident.Status = IncidentStatus.RESOLVED;
        incident.ResolvedByUserId = adminOrStaffUserId;
        incident.ResolvedAt = DateTime.UtcNow;
        incident.ResolutionNote = $"Mở khóa từ xa (Emergency). Lý do Audit: {cleanReason}";

        await _db.SaveChangesAsync();

        _logger.LogWarning("🔓 [REMOTE UNLOCK] Đã mở tủ từ xa cho sự cố {IncidentCode}. Bởi: {UserEmail}. Lý do: {Reason}",
            incident.IncidentCode, userEmail, cleanReason);

        // Ghi nhận audit log
        await _auditLog.LogAsync(
            adminOrStaffUserId,
            userEmail,
            userRole,
            "REMOTE_EMERGENCY_UNLOCK",
            "Locker",
            incident.LockerId.ToString(),
            $"Mở tủ khẩn cấp từ xa cho sự cố {incident.IncidentCode} (Tủ {incident.Locker?.LockerCode}). Lý do Audit: {cleanReason}"
        );

        return ApiResponse<bool>.SuccessResponse(true, $"Đã mở khóa tủ từ xa thành công cho sự cố {incident.IncidentCode}");
    }

    public async Task<ApiResponse<bool>> DirectRemoteUnlockLockerAsync(Guid lockerId, RemoteUnlockRequest request, Guid adminOrStaffUserId, string? userEmail, string? userRole)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 5)
            return ApiResponse<bool>.ErrorResponse("Bắt buộc nhập lý do mở tủ khẩn cấp (Audit Reason tối thiểu 5 ký tự) theo quy định an ninh UC-S05.");

        var locker = await _db.Lockers.FirstOrDefaultAsync(l => l.Id == lockerId);
        if (locker == null)
            return ApiResponse<bool>.ErrorResponse("Không tìm thấy ô tủ");

        var cleanReason = request.Reason.Trim();

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
            Description = $"Mở tủ từ xa trực tiếp: {cleanReason}",
            RemoteUnlocked = true,
            ResolvedByUserId = adminOrStaffUserId,
            ResolvedAt = DateTime.UtcNow,
            ResolutionNote = $"Mở khóa từ xa trực tiếp. Lý do Audit: {cleanReason}",
            CreatedAt = DateTime.UtcNow
        };

        _db.SecurityIncidents.Add(incident);

        // Phát lệnh MQTT xuống ESP32
        string deviceCode = "ESP32-01";
        if (locker.IoTDeviceId.HasValue)
        {
            var iot = await _db.IoTDevices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == locker.IoTDeviceId.Value);
            if (!string.IsNullOrWhiteSpace(iot?.DeviceCode))
            {
                deviceCode = iot.DeviceCode;
            }
        }

        try
        {
            await _mqttPublish.PublishUnlockCommandAsync(
                deviceCode,
                locker.Id,
                locker.LockerCode,
                $"EMG-{incident.IncidentCode}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Không thể gửi lệnh MQTT cho thiết bị {DeviceCode}, tiếp tục mở khóa trạng thái DB", deviceCode);
        }

        locker.DoorState = "OPEN";
        locker.LastDoorEventAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogWarning("🔓 [DIRECT REMOTE UNLOCK] Đã mở tủ {LockerCode} từ xa. Bởi: {UserEmail}. Lý do: {Reason}",
            locker.LockerCode, userEmail, cleanReason);

        await _auditLog.LogAsync(
            adminOrStaffUserId,
            userEmail,
            userRole,
            "DIRECT_REMOTE_UNLOCK",
            "Locker",
            lockerId.ToString(),
            $"Mở tủ {locker.LockerCode} từ xa trực tiếp (Sự cố {incident.IncidentCode}). Lý do Audit: {cleanReason}"
        );

        return ApiResponse<bool>.SuccessResponse(true, $"Đã mở khóa tủ {locker.LockerCode} từ xa thành công");
    }
}
