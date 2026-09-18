namespace SmartLocker.Application.DTOs.Auth;

/// <summary>
/// Request đăng ký tài khoản mới.
/// </summary>
public class RegisterRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
    public bool AgreeTerms { get; set; }
}

/// <summary>
/// Request verify OTP khi đăng ký hoặc quên mật khẩu.
/// </summary>
public class VerifyOtpRequest
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Purpose { get; set; } = "Register"; // Register | ForgotPassword
}

/// <summary>
/// Request resend OTP khi mã cũ hết hạn hoặc không nhận được.
/// </summary>
public class ResendOtpRequest
{
    public string Email { get; set; } = string.Empty;
    public string Purpose { get; set; } = "Register";
}

/// <summary>
/// Request đăng nhập bằng email + password.
/// </summary>
public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

/// <summary>
/// Request bắt đầu quên mật khẩu (gửi OTP tới email).
/// </summary>
public class ForgotPasswordRequest
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Request đặt lại mật khẩu mới sau khi đã verify OTP.
/// </summary>
public class ResetPasswordRequest
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// Response trả về sau khi login/register thành công.
/// </summary>
public class AuthResponse
{
    public Guid UserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime AccessTokenExpiresAt { get; set; }
}

/// <summary>
/// Response thông báo đã gửi OTP thành công.
/// </summary>
public class OtpSentResponse
{
    public string Email { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public int ResendCountdown { get; set; } // Số giây được gửi lại (thường 60s)
}

/// <summary>
/// Request đăng nhập bằng Google OAuth 2.0.
/// Frontend gửi id_token nhận từ Google Sign-In.
/// </summary>
public class GoogleLoginRequest
{
    public string IdToken { get; set; } = string.Empty;
}
