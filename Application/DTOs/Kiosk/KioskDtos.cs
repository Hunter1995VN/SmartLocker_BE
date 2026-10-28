namespace Application.DTOs.Kiosk;

public class KioskAuthRequest
{
    public string DeviceCode { get; set; } = string.Empty;
    public string? SecretKey { get; set; }
}

public class KioskAuthResponse
{
    public bool Success { get; set; }
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public class KioskCheckInRequest
{
    public Guid StationId { get; set; }
    public string CredentialType { get; set; } = "QR"; // "QR" or "ACCESS_CODE"
    public string Payload { get; set; } = string.Empty;
}

public class KioskCheckInResponse
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid LockerId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public string LockerSize { get; set; } = string.Empty;
    public string DoorState { get; set; } = "OPEN";
    public string Message { get; set; } = string.Empty;
}

public class KioskCheckOutRequest
{
    public Guid StationId { get; set; }
    public string CredentialType { get; set; } = "QR"; // "QR" or "ACCESS_CODE"
    public string Payload { get; set; } = string.Empty;
}

public class KioskCheckOutResponse
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid? LockerId { get; set; }
    public string? LockerCode { get; set; }
    public string? LockerSize { get; set; }
    public bool IsOverdue { get; set; }
    public decimal OverdueFee { get; set; }
    public string? CheckoutUrl { get; set; }
    public string? DoorState { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class KioskManualConfirmRequest
{
    public Guid StationId { get; set; }
    public Guid BookingId { get; set; }
    public Guid LockerId { get; set; }
    public string? PhotoBase64 { get; set; }
    public string? Notes { get; set; }
}

public class KioskLockerItemDto
{
    public Guid LockerId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string BusinessStatus { get; set; } = string.Empty;
    public string HealthStatus { get; set; } = string.Empty;
    public string DoorState { get; set; } = "UNKNOWN";
}

public class KioskStationStatusResponse
{
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string StationAddress { get; set; } = string.Empty;
    public string ConnectivityStatus { get; set; } = "ONLINE";
    public List<KioskLockerItemDto> Lockers { get; set; } = new();
}
