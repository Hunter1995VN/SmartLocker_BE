namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[ApiController]
[Route("api/admin/refunds")]
[Authorize(Roles = "ADMIN,STAFF,Admin,Staff")]
public class AdminRefundsController : ControllerBase
{
    private readonly IAdminRefundService _refundService;

    public AdminRefundsController(IAdminRefundService refundService)
    {
        _refundService = refundService;
    }

    [HttpGet]
    public async Task<IActionResult> GetRefundRequests([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _refundService.GetRefundRequestsAsync(status, page, pageSize);
        return Ok(result);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> ApproveRefund(Guid id, [FromBody] ProcessRefundRequest? request)
    {
        var adminId = GetUserId();
        var result = await _refundService.ApproveRefundAsync(id, adminId, request);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> RejectRefund(Guid id, [FromBody] ProcessRefundRequest? request)
    {
        var adminId = GetUserId();
        var result = await _refundService.RejectRefundAsync(id, adminId, request);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    private Guid GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User.FindFirst("sub")?.Value
                    ?? User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? id : Guid.Empty;
    }
}

