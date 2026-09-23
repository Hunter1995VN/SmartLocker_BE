namespace Application.Interfaces;

public interface IAuditLogService
{
    Task LogAsync(Guid? userId, string? userEmail, string? userRole, string action, string targetEntity, string? targetId, string? details, string? ipAddress = null);
}
