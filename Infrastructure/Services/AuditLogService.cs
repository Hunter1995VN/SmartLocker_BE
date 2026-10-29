namespace Infrastructure.Services;

using Application.DTOs.Admin;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// UC-A08: Tra cứu nhật ký truy vết an ninh toàn hệ thống (Audit Logs)
    /// </summary>
    public async Task<ApiResponse<AuditLogQueryResponse>> GetAuditLogsAsync(
        string? search = null,
        string? action = null,
        string? targetEntity = null,
        string? role = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int page = 1,
        int pageSize = 50)
    {
        var baseQuery = _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action != "USER_NOTIFICATION_PREFS" && a.Action != "PROMO_META_SNAPSHOT" && a.Action != "MANUAL_REFUND_META");

        // Thống kê tổng quan trên toàn bộ AuditLogs an ninh
        var totalAll = await baseQuery.CountAsync();
        var unlockCount = await baseQuery.CountAsync(a => a.Action.Contains("UNLOCK"));
        var policyCount = await baseQuery.CountAsync(a => a.Action.Contains("PRICING") || a.Action.Contains("STATION") || a.Action.Contains("LOCKER") || a.Action.Contains("RECONCIL") || a.Action.Contains("REFUND") || a.Action.Contains("VOUCHER") || a.Action.Contains("CAMPAIGN") || a.Action.Contains("PROMO"));
        var userSecCount = await baseQuery.CountAsync(a => a.Action.Contains("USER") || a.Action.Contains("STAFF") || a.Action.Contains("DEBT") || a.Action.Contains("LOGIN"));
        var abandonedCount = await baseQuery.CountAsync(a => a.Action.Contains("ABANDONED"));

        var query = baseQuery.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var kw = search.Trim();
            query = query.Where(a =>
                (a.Details != null && a.Details.Contains(kw)) ||
                (a.UserEmail != null && a.UserEmail.Contains(kw)) ||
                a.Action.Contains(kw) ||
                a.TargetEntity.Contains(kw) ||
                (a.TargetId != null && a.TargetId.Contains(kw)));
        }

        if (!string.IsNullOrWhiteSpace(action) && action != "ALL")
        {
            query = query.Where(a => a.Action.Contains(action));
        }

        if (!string.IsNullOrWhiteSpace(targetEntity) && targetEntity != "ALL")
        {
            query = query.Where(a => a.TargetEntity == targetEntity);
        }

        if (!string.IsNullOrWhiteSpace(role) && role != "ALL")
        {
            query = query.Where(a => a.UserRole == role);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(a => a.CreatedAt >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            var endOfDay = toDate.Value.Date.AddDays(1);
            query = query.Where(a => a.CreatedAt < endOfDay);
        }

        var filteredCount = await query.CountAsync();
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 200);

        var rawLogs = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync();

        var userIds = rawLogs
            .Where(l => l.UserId.HasValue)
            .Select(l => l.UserId!.Value)
            .Distinct()
            .ToList();

        var userDict = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new { u.FullName, u.Email, Role = u.Role.ToString() });

        var items = rawLogs.Select(l =>
        {
            var uData = l.UserId.HasValue && userDict.TryGetValue(l.UserId.Value, out var tmp) ? tmp : null;

            return new AuditLogItemDto
            {
                Id = l.Id,
                UserId = l.UserId,
                UserName = uData?.FullName ?? (string.IsNullOrWhiteSpace(l.UserEmail) ? "Hệ thống / Quản trị viên" : l.UserEmail),
                UserEmail = !string.IsNullOrWhiteSpace(l.UserEmail) ? l.UserEmail : (uData?.Email ?? "system@smartlocker.vn"),
                UserRole = !string.IsNullOrWhiteSpace(l.UserRole) ? l.UserRole : (uData?.Role ?? "ADMIN"),
                Action = l.Action,
                TargetEntity = l.TargetEntity,
                TargetId = l.TargetId,
                Details = l.Details,
                IpAddress = l.IpAddress ?? "10.0.14.22",
                CreatedAt = l.CreatedAt
            };
        }).ToList();

        var response = new AuditLogQueryResponse
        {
            TotalCount = filteredCount > 0 || !string.IsNullOrWhiteSpace(search) || !string.IsNullOrWhiteSpace(action) ? filteredCount : totalAll,
            EmergencyUnlockCount = unlockCount,
            PolicyAndStationChangeCount = policyCount,
            UserAndSecurityCount = userSecCount,
            AbandonedPropertyCount = abandonedCount,
            Page = safePage,
            PageSize = safePageSize,
            Items = items
        };

        return ApiResponse<AuditLogQueryResponse>.SuccessResponse(response);
    }
}
