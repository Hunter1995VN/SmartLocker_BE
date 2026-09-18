namespace SmartLocker.Domain.Entities;

/// <summary>
/// Bảng OtpCodes - Lưu mã OTP dùng để xác thực đăng ký, đăng nhập, quên mật khẩu.
/// </summary>
public class OtpCode
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }                    // FK → Users.Id (nullable)
    public string Recipient { get; set; } = string.Empty; // Email hoặc SĐT - map từ [Recipient]
    public string Purpose { get; set; } = string.Empty;   // REGISTER, CHANGE_EMAIL, CHANGE_PHONE, RESET_PASSWORD
    public string CodeHash { get; set; } = string.Empty;  // SHA256 hash của mã OTP
    public short Attempts { get; set; } = 0;              // Map từ [Attempts]
    public short MaxAttempts { get; set; } = 5;
    public short ResendCount { get; set; } = 0;
    public DateTime? LockedUntil { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }                // Map từ [UsedAt]
    public string IpAddress { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }              // Map từ [CreatedAt]

    // Navigation property
    public User? User { get; set; }

    public bool IsExpired() => DateTime.UtcNow > ExpiresAt;
    public bool IsUsed() => UsedAt.HasValue;
    public bool CanRetry() => Attempts < MaxAttempts && !IsExpired() && !IsUsed();
}
