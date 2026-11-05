namespace Application.DTOs.Admin;

using Domain.Enums;

public class LockerGridItemDto
{
    public Guid Id { get; set; }
    public Guid StationId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty; // S, M, L
    public short? GpioPin { get; set; }
    public string BusinessStatus { get; set; } = string.Empty; // AVAILABLE, RESERVED, OCCUPIED, MAINTENANCE, DISABLED
    public string HealthStatus { get; set; } = string.Empty; // HEALTHY, WARNING, CRITICAL
    public string DoorState { get; set; } = "UNKNOWN"; // OPEN, CLOSED, UNKNOWN
    public DateTime? LastDoorEventAt { get; set; }
    public Guid? IoTDeviceId { get; set; }
    public string ConnectivityStatus { get; set; } = "UNMAPPED";
    public DateTime? LastSeenAt { get; set; }
    public string? DeviceCode { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? Notes { get; set; }
    public ActiveBookingBriefDto? CurrentBooking { get; set; }
}

public class ActiveBookingBriefDto
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsOverdue { get; set; }
}

public class UpdateLockerStatusRequest
{
    public LockerBusinessStatus BusinessStatus { get; set; }
    public LockerHealthStatus HealthStatus { get; set; }
    public string? Reason { get; set; }
}

public class CreateMaintenanceTicketRequest
{
    public Guid? LockerId { get; set; }
    public Guid? IoTDeviceId { get; set; }
    public string IssueDescription { get; set; } = string.Empty;
    public string Priority { get; set; } = "MEDIUM";
    public Guid? AssignedStaffId { get; set; }
}

public class SwapSpareLockerRequest
{
    public string Reason { get; set; } = "Kẹt chốt cơ học / Cửa không bung";
    public bool OpenNewLocker { get; set; } = true;
}

public class SwapSpareLockerResultDto
{
    public Guid OldLockerId { get; set; }
    public string OldLockerCode { get; set; } = string.Empty;
    public Guid NewLockerId { get; set; }
    public string NewLockerCode { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public Guid? BookingId { get; set; }
    public string? BookingCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime SwappedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// UC-A02: Cấu hình danh mục ô tủ và ánh xạ mã chân bo mạch IoT
/// </summary>
public class UpdateLockerHardwareMappingRequest
{
    public short? GpioPin { get; set; }
    public Guid? IoTDeviceId { get; set; }
}

/// <summary>
/// UC-S03: Giám sát cảm biến cửa đóng/mở & Lịch sử Telemetry
/// </summary>
public class DoorTelemetryItemDto
{
    public Guid LockerId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string DoorState { get; set; } = "CLOSED";
    public DateTime? LastDoorEventAt { get; set; }
    public short? GpioPin { get; set; }
    public string HealthStatus { get; set; } = "HEALTHY";
    public string DeviceCode { get; set; } = string.Empty;
}
