namespace Application.DTOs.Booking;

/// <summary>
/// DTO chứa thông tin vé mở khóa ngoại tuyến (Offline HMAC Token).
/// </summary>
public class OfflineTicketDto
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string StationAddress { get; set; } = string.Empty;
    public Guid? LockerId { get; set; }
    public string? LockerCode { get; set; }
    public string LockerSize { get; set; } = string.Empty;
    public string AccessPin { get; set; } = string.Empty;
    public string OfflineHmacToken { get; set; } = string.Empty;
    public string QrData { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public DateTime ValidUntil { get; set; }
    public bool IsOfflineReady { get; set; } = true;
    public string SecurityAlgorithm { get; set; } = "HMAC-SHA256 (JWS RFC 7515)";
    public string Instructions { get; set; } = "Mã QR và PIN này được mã hóa bảo mật HMAC. Bạn có thể quét trực tiếp tại màn hình trạm SmartLocker hoặc nhập mã PIN 6 số mà không cần kết nối mạng 4G/Wifi.";
}
