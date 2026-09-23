namespace Domain.Entities;

public class IoTDevice
{
    public Guid Id { get; set; }
    public Guid StationId { get; set; }
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "MASTER_BOARD";
    public string MacAddress { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string FirmwareVersion { get; set; } = "1.0.0";
    public string Status { get; set; } = "ONLINE"; // ONLINE, OFFLINE, ERROR, MAINTENANCE
    public DateTime? LastPingAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual Station Station { get; set; } = null!;
    public virtual ICollection<Locker> Lockers { get; set; } = new List<Locker>();
}
