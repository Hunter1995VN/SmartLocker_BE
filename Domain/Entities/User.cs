namespace Domain.Entities;

using Domain.Enums;

public class User
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.TRAVELER;
    public UserStatus Status { get; set; } = UserStatus.PENDING_VERIFICATION;
    public decimal OverdueDebt { get; set; }
    // Thêm từ module Auth (Quân): Google OAuth và Avatar
    public string? GoogleId { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
