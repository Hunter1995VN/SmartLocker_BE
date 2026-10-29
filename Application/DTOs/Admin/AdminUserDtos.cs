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

public class UpdateInternalUserRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public UserRole? Role { get; set; }
    public string? Reason { get; set; }
}

public class ResetInternalPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
    public string? Reason { get; set; }
}

public class DeactivateInternalUserRequest
{
    public string? Reason { get; set; }
}

public class UpdateUserStatusRequest
{
    public UserStatus Status { get; set; }
    public string? Reason { get; set; }
}

public class UpdateUserRoleRequest
{
    public UserRole Role { get; set; }
    public string? Reason { get; set; }
}

public class UpdateUserDebtRequest
{
    public decimal OverdueDebt { get; set; }
    public string? Reason { get; set; }
    public bool AutoSuspendIfDebt { get; set; } = true;
}

public class BlockDebtorsRequest
{
    public string? Reason { get; set; }
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
    public string? AvatarUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int ActionsCount { get; set; }
}

public class RbacPermissionItemDto
{
    public string Code { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool AdminAllowed { get; set; }
    public bool StaffAllowed { get; set; }
    public bool TravelerAllowed { get; set; }
    public string StaffScopeNote { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = "LOW"; // LOW, MEDIUM, HIGH, CRITICAL
}

public class RbacRoleOverviewDto
{
    public string Role { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int TotalUsers { get; set; }
    public int ActiveUsers { get; set; }
    public int SuspendedUsers { get; set; }
    public List<string> KeyPermissions { get; set; } = new();
    public List<string> Restrictions { get; set; } = new();
}

public class RbacSummaryResponseDto
{
    public int TotalInternalAccounts { get; set; }
    public int TotalAdmins { get; set; }
    public int ActiveAdmins { get; set; }
    public int TotalStaff { get; set; }
    public int ActiveStaff { get; set; }
    public int SuspendedInternalAccounts { get; set; }
    public int DeactivatedInternalAccounts { get; set; }
    public int TotalTravelers { get; set; }
    public List<RbacRoleOverviewDto> Roles { get; set; } = new();
    public List<RbacPermissionItemDto> PermissionMatrix { get; set; } = new();
    public List<AuditLogItemDto> RecentRbacLogs { get; set; } = new();
}
