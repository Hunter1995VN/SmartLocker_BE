namespace API.Controllers.Admin;

using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// UC-A08: Tra cứu nhật ký truy vết an ninh toàn hệ thống (Audit Logs)
/// </summary>
[ApiController]
[Route("api/admin/audit-logs")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminAuditLogsController : ControllerBase
{
    private readonly IAuditLogService _auditLogService;

    public AdminAuditLogsController(IAuditLogService auditLogService)
    {
        _auditLogService = auditLogService;
    }

    /// <summary>
    /// GET /api/admin/audit-logs — Tra cứu nhật ký truy vết an ninh toàn hệ thống (filter: search, action, targetEntity, role, fromDate, toDate)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAuditLogs(
        [FromQuery] string? search = null,
        [FromQuery] string? action = null,
        [FromQuery] string? targetEntity = null,
        [FromQuery] string? role = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var result = await _auditLogService.GetAuditLogsAsync(
            search, action, targetEntity, role, fromDate, toDate, page, pageSize);
        return Ok(result);
    }
}
