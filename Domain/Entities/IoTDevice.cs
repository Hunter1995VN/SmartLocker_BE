namespace Domain.Entities;

public class IoTDevice
{
    public Guid Id { get; set; }
    public Guid StationId { get; set; }
    public string DeviceCode { get; set; } = string.Empty;
    public string? DeviceSecretHash { get; set; }
    public string DeviceType { get; set; } = "MASTER_BOARD";
    public string? MqttClientId { get; set; }
    public string FirmwareVersion { get; set; } = "1.0.0";
    public string ConnectivityStatus { get; set; } = "ONLINE"; // ONLINE, OFFLINE, DEGRADED
    public DateTime? LastSeenAt { get; set; }
    public int? RtcOffsetMs { get; set; }
    public DateTime? RtcLastSyncedAt { get; set; }
    public short? WifiRssi { get; set; }
    public long? UptimeSeconds { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual Station Station { get; set; } = null!;
    public virtual ICollection<Locker> Lockers { get; set; } = new List<Locker>();
}
