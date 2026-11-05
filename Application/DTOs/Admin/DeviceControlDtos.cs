namespace Application.DTOs.Admin;

public class DeviceControlResultDto
{
    public string DeviceCode { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

public class DiagnosticsResultDto
{
    public int SchemaVersion { get; set; } = 1;
    public string DeviceCode { get; set; } = string.Empty;
    public string Status { get; set; } = "HEALTHY";
    public int DurationMs { get; set; }
    public Dictionary<string, string> Relays { get; set; } = new();
    public Dictionary<string, string> Doors { get; set; } = new();
    public short WifiRssi { get; set; }
    public long FreeHeapBytes { get; set; }
    public long UptimeSeconds { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}

public class IoTDeviceDto
{
    public Guid Id { get; set; }
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string StationAddress { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "MASTER_BOARD";
    public string FirmwareVersion { get; set; } = "1.0.0";
    public string ConnectivityStatus { get; set; } = "ONLINE";
    public short? WifiRssi { get; set; }
    public long? UptimeSeconds { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsProvisioned { get; set; } = true;
}
