namespace Domain.Entities;

using Domain.Enums;

public class Locker
{
    public Guid Id { get; set; }
    public Guid StationId { get; set; }
    public Guid? IoTDeviceId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public LockerSize Size { get; set; }
    public short? GpioPin { get; set; }
    public LockerBusinessStatus BusinessStatus { get; set; } = LockerBusinessStatus.AVAILABLE;
    public LockerHealthStatus HealthStatus { get; set; } = LockerHealthStatus.HEALTHY;
    public string DoorState { get; set; } = "UNKNOWN";
    public DateTime? LastDoorEventAt { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public virtual Station Station { get; set; } = null!;
    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
