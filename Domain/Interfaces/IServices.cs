namespace SmartLocker.Domain.Interfaces;

/// <summary>
/// Giao diện trừu tượng cho dịch vụ gửi OTP qua kênh Email / SMS.
/// </summary>
public interface IOtpSender
{
    /// <summary>
    /// Gửi mã OTP tới số điện thoại hoặc email.
    /// </summary>
    Task SendAsync(string destination, string code, CancellationToken ct = default);

    /// <summary>
    /// Gửi email thông báo đăng ký thành công.
    /// </summary>
    Task SendWelcomeAsync(string email, string fullName, CancellationToken ct = default);
}

/// <summary>
/// Giao diện hash + verify mật khẩu (BCrypt). Tách interface để dễ test.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

/// <summary>
/// Sinh mã OTP ngẫu nhiên 6 số, có thể test thay thế bằng mock.
/// </summary>
public interface IOtpGenerator
{
    string Generate();
}

/// <summary>
/// Xác thực Google OAuth id_token.
/// </summary>
public interface IGoogleAuthService
{
    Task<GoogleUserPayload?> VerifyIdTokenAsync(string idToken, CancellationToken ct = default);
}

/// <summary>
/// Dữ liệu user từ Google sau khi verify token.
/// </summary>
public class GoogleUserPayload
{
    public string Subject { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Picture { get; set; }
}
