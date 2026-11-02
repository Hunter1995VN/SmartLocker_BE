namespace API.Controllers.Admin;

using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-A12: Đối soát tài chính định kỳ giữa cổng PayOS và cơ sở dữ liệu hệ thống
/// </summary>
[ApiController]
[Route("api/admin/reconciliation")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminReconciliationController : ControllerBase
{
    private readonly IAdminReconciliationService _reconciliationService;

    public AdminReconciliationController(IAdminReconciliationService reconciliationService)
    {
        _reconciliationService = reconciliationService;
    }

    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    private string? GetUserEmail() => User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
    private string? GetUserRole() => User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("role");

    /// <summary>
    /// GET /api/admin/reconciliation — Bảng tổng hợp đối soát tài chính PayOS & CSDL (UC-A12)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetReconciliationDashboard(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? kind = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var result = await _reconciliationService.GetReconciliationDashboardAsync(
            search, status, kind, fromDate, toDate);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/reconciliation/run — Kích hoạt chu trình đối soát hàng loạt với cổng PayOS (UC-A12)
    /// </summary>
    [HttpPost("run")]
    public async Task<IActionResult> RunBatchReconciliation()
    {
        var userId = GetUserId();
        var result = await _reconciliationService.RunBatchReconciliationAsync(
            userId, GetUserEmail(), GetUserRole());
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/reconciliation/sync/{orderCode} — Đối soát trực tiếp 1 mã giao dịch PayOS cụ thể
    /// </summary>
    [HttpPost("sync/{orderCode:long}")]
    public async Task<IActionResult> ReconcileSingleOrder(long orderCode)
    {
        var userId = GetUserId();
        var result = await _reconciliationService.ReconcileSingleOrderAsync(
            orderCode, userId, GetUserEmail(), GetUserRole());
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }
}
