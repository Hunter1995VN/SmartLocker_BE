namespace SmartLocker.Domain.Entities;

/// <summary>
/// Bảng Users - Lưu trữ thông tin tài khoản người dùng hệ thống SmartLocker
/// </summary>
public class User
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "TRAVELER"; // STAFF | ADMIN | TRAVELER
    public string Status { get; set; } = "PENDING_VERIFICATION"; // PENDING_VERIFICATION | ACTIVE | SUSPENDED | DELETED
    public decimal OverdueDebt { get; set; } = 0m;
    public string? GoogleId { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
