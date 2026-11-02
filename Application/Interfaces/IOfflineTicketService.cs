namespace Application.Interfaces;

using Application.DTOs.Common;
using Application.DTOs.Booking;
using Domain.Entities;

public class ParsedOfflineTicket
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public string? StationName { get; set; }
    public Guid? LockerId { get; set; }
    public string? LockerCode { get; set; }
    public string? LockerSize { get; set; }
    public string AccessPin { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Nonce { get; set; } = string.Empty;
}

public interface IOfflineTicketService
{
    /// <summary>
    /// Lấy hoặc phát hành vé mở khóa ngoại tuyến có chữ ký HMAC-SHA256 cho người dùng
    /// </summary>
    Task<ApiResponse<OfflineTicketDto>> GetOfflineTicketAsync(Guid userId, Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Tạo token HMAC-SHA256 chuẩn JWS RFC 7515 (Compact: header.payload.signature)
    /// </summary>
    string GenerateHmacToken(Booking booking, string pin, DateTime expiresAt, string nonce);

    /// <summary>
    /// Xác thực token HMAC ngoại tuyến bằng khóa bí mật hệ thống và kiểm tra hạn sử dụng
    /// </summary>
    (bool IsValid, ParsedOfflineTicket? Ticket, string? Error) VerifyHmacToken(string token);

    /// <summary>
    /// Trích xuất mã PIN 6 số hiển thị từ AccessCredential (giải mã từ token hoặc lấy trực tiếp)
    /// </summary>
    string ExtractPinFromCredential(AccessCredential credential);
}
