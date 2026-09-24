namespace Infrastructure.Services;

using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.Logging;

public class AuditLogService : IAuditLogService
{
    private readonly ISmartLockerDbContext _db;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(ISmartLockerDbContext db, ILogger<AuditLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(Guid? userId, string? userEmail, string? userRole, string action, string targetEntity, string? targetId, string? details, string? ipAddress = null)
    {
        try
        {
            var log = new AuditLog
            {
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
            _logger.LogInformation("📋 [AUDIT] {Action} by {UserEmail} ({UserRole}) on {TargetEntity} ({TargetId})", action, userEmail, userRole, targetEntity, targetId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "⚠️ Không thể ghi AuditLog cho action {Action}: {Message}", action, ex.Message);
        }
    }
}
