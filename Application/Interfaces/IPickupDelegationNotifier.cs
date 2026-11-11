namespace Application.Interfaces;

/// <summary>
/// Kết quả chuyển mã ủy quyền tới người nhận (UC-T16).
/// Channel: EMAIL khi liên hệ là địa chỉ email; MANUAL khi chưa có kênh tự động (ví dụ số điện thoại) và chủ đơn phải tự chuyển mã.
/// Sent chỉ true khi máy chủ SMTP đã nhận thư; Reason giải thích khi Sent = false.
/// </summary>
public sealed record DelegationDelivery(string Channel, bool Sent, string? Reason);

public interface IPickupDelegationNotifier
{
    /// <summary>
    /// Gửi mã 8 số dùng một lần cho người nhận ủy quyền. Không bao giờ ném lỗi ra ngoài:
    /// lỗi gửi được phản ánh trong <see cref="DelegationDelivery"/> để việc tạo ủy quyền không bị hủy.
    /// </summary>
    Task<DelegationDelivery> SendAsync(string contact, string pin, DateTime expiresAtUtc, string stationName, string bookingCode, CancellationToken ct = default);
}
