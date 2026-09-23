namespace Infrastructure.Services;

using Application.Interfaces;
using Domain.Entities;

public class AuditLogService : IAuditLogService
{
    private readonly ISmartLockerDbContext _db;

    public AuditLogService(ISmartLockerDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(Guid? userId, string? userEmail, string? userRole, string action, string targetEntity, string? targetId, string? details, string? ipAddress = null)
    {
        var log = new AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            UserEmail = userEmail,
            UserRole = userRole,
            Action = action,
            TargetEntity = targetEntity,
            TargetId = targetId,
            Details = details,
            IpAddress = ipAddress,
            CreatedAt = DateTime.UtcNow
        };

        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync();
    }
}
