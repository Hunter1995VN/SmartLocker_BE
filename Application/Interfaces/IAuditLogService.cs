namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAuditLogService
{
    Task LogAsync(Guid? userId, string? userEmail, string? userRole, string action, string targetEntity, string? targetId, string? details, string? ipAddress = null);

    Task<ApiResponse<AuditLogQueryResponse>> GetAuditLogsAsync(
        string? search = null,
        string? action = null,
        string? targetEntity = null,
        string? role = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int page = 1,
        int pageSize = 50);
}
