namespace Application.DTOs.Admin;

using Domain.Enums;

public class CreateStaffRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.STAFF;
}

public class UpdateUserStatusRequest
{
    public UserStatus Status { get; set; }
    public string? Reason { get; set; }
}

public class UpdateUserRoleRequest
{
    public UserRole Role { get; set; }
}

public class UserAdminListItemDto
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal OverdueDebt { get; set; }
    public DateTime CreatedAt { get; set; }
}
