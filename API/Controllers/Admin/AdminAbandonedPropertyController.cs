namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-S10, UC-S11, UC-A07: Process Abandoned Property — Kiểm kê niêm phong 72h, Phê duyệt 4 mắt & Bàn giao đồ thất lạc
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
    /// GET /api/admin/abandoned-properties/overdue-candidates — Danh sách đơn đặt tủ đang lưu trữ / quá hạn để chọn lập biên bản (UC-S10)
    /// </summary>
    [HttpGet("overdue-candidates")]
    public async Task<IActionResult> GetOverdueCandidates()
    {
        var result = await _abandonedPropertyService.GetOverdueCandidatesAsync();
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/abandoned-properties — Mắt 1: Staff lập biên bản niêm phong đồ quá hạn 72h (UC-S10)
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
    /// POST /api/admin/abandoned-properties/{recordId}/approve — Mắt 2: Admin phê duyệt biên bản (UC-A07)
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
    /// POST /api/admin/abandoned-properties/{recordId}/reject — Admin từ chối biên bản kiểm kê (UC-A07)
    /// </summary>
    [HttpPost("{recordId:guid}/reject")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> RejectRecord(Guid recordId, [FromBody] RejectAbandonedPropertyRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _abandonedPropertyService.RejectRecordAsync(recordId, request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/abandoned-properties/{recordId}/resolve — Thủ tục bàn giao lại đồ thất lạc cho khách (UC-S11) hoặc hoàn tất thanh lý (UC-A07)
    /// </summary>
    [HttpPost("{recordId:guid}/resolve")]
    public async Task<IActionResult> ResolveRecord(Guid recordId, [FromBody] ResolveAbandonedPropertyDetailRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _abandonedPropertyService.ResolveRecordDetailAsync(recordId, request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }
}
