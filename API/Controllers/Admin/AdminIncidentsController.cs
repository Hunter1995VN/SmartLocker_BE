namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-T14 ➜ A08, S-02: Emergency Center / Incident Management
/// Tiếp nhận sự cố, xử lý mở tủ từ xa (Remote Emergency Unlock)
/// </summary>
[ApiController]
[Route("api/admin/incidents")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminIncidentsController : ControllerBase
{
    private readonly IAdminIncidentService _incidentService;

    public AdminIncidentsController(IAdminIncidentService incidentService)
    {
        _incidentService = incidentService;
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
    /// GET /api/admin/incidents — Danh sách tất cả sự cố (filter: status, severity)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetIncidents(
        [FromQuery] string? status = null,
        [FromQuery] string? severity = null)
    {
        var result = await _incidentService.GetIncidentsAsync(status, severity);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/incidents — Tạo báo cáo sự cố mới
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateIncident([FromBody] CreateIncidentRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _incidentService.CreateIncidentAsync(request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/incidents/{incidentId}/remote-unlock — Mở tủ từ xa theo sự cố (Emergency Unlock)
    /// </summary>
    [HttpPost("{incidentId:guid}/remote-unlock")]
    public async Task<IActionResult> RemoteUnlockEmergency(Guid incidentId, [FromBody] RemoteUnlockRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _incidentService.RemoteUnlockEmergencyAsync(incidentId, request, userId, GetUserEmail(), GetUserRole());
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/incidents/lockers/{lockerId}/remote-unlock — Mở tủ từ xa trực tiếp (không cần incident trước)
    /// </summary>
    [HttpPost("lockers/{lockerId:guid}/remote-unlock")]
    public async Task<IActionResult> DirectRemoteUnlockLocker(Guid lockerId, [FromBody] RemoteUnlockRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _incidentService.DirectRemoteUnlockLockerAsync(lockerId, request, userId, GetUserEmail(), GetUserRole());
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }
}
