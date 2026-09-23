namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-A09, S-03: Process Abandoned Property — Kiểm kê tài sản bỏ quên (Nguyên tắc 4 mắt)
/// </summary>
[ApiController]
[Route("api/admin/abandoned-properties")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminAbandonedPropertyController : ControllerBase
{
    private readonly IAdminAbandonedPropertyService _abandonedPropertyService;

    public AdminAbandonedPropertyController(IAdminAbandonedPropertyService abandonedPropertyService)
    {
        _abandonedPropertyService = abandonedPropertyService;
    }

    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    /// <summary>
    /// GET /api/admin/abandoned-properties — Danh sách biên bản tài sản bỏ quên
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAbandonedProperties([FromQuery] string? status = null)
    {
        var result = await _abandonedPropertyService.GetAbandonedPropertiesAsync(status);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/abandoned-properties — Mắt 1: Staff lập biên bản kiểm kê
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "ADMIN,STAFF")]
    public async Task<IActionResult> CreateRecord([FromBody] CreateAbandonedPropertyRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _abandonedPropertyService.CreateRecordAsync(request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/abandoned-properties/{recordId}/approve — Mắt 2: Admin phê duyệt biên bản
    /// </summary>
    [HttpPost("{recordId:guid}/approve")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> ApproveRecord(Guid recordId, [FromBody] ApproveAbandonedPropertyRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _abandonedPropertyService.ApproveRecordAsync(recordId, request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/abandoned-properties/{recordId}/resolve — Hoàn tất xử lý (lưu kho/thanh lý/trả khách)
    /// </summary>
    [HttpPost("{recordId:guid}/resolve")]
    public async Task<IActionResult> ResolveRecord(Guid recordId, [FromBody] ResolveAbandonedPropertyRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _abandonedPropertyService.ResolveRecordAsync(recordId, request.DisposalAction, request.Notes, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }
}

public class ResolveAbandonedPropertyRequest
{
    public string DisposalAction { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
