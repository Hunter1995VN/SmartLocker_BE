namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-A05, S-01: Locker Visual Grid & Status Control — Sơ đồ ô tủ trực quan + bảo trì
/// </summary>
[ApiController]
[Route("api/admin/lockers")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminLockersController : ControllerBase
{
    private readonly IAdminStationLockerService _stationLockerService;

    public AdminLockersController(IAdminStationLockerService stationLockerService)
    {
        _stationLockerService = stationLockerService;
    }

    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    /// <summary>
    /// GET /api/admin/lockers/station/{stationId}/grid — Sơ đồ ô tủ theo trạm (Locker Grid)
    /// </summary>
    [HttpGet("station/{stationId:guid}/grid")]
    public async Task<IActionResult> GetStationLockersGrid(Guid stationId)
    {
        var result = await _stationLockerService.GetStationLockersGridAsync(stationId);
        return Ok(result);
    }

    /// <summary>
    /// PATCH /api/admin/lockers/{lockerId}/status — Đổi trạng thái ô tủ (MAINTENANCE, DISABLED...)
    /// </summary>
    [HttpPatch("{lockerId:guid}/status")]
    public async Task<IActionResult> UpdateLockerStatus(Guid lockerId, [FromBody] UpdateLockerStatusRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _stationLockerService.UpdateLockerStatusAsync(lockerId, request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/lockers/maintenance-tickets — Tạo phiếu bảo trì cho ô tủ hoặc thiết bị
    /// </summary>
    [HttpPost("maintenance-tickets")]
    public async Task<IActionResult> CreateMaintenanceTicket([FromBody] CreateMaintenanceTicketRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _stationLockerService.CreateMaintenanceTicketAsync(request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }
}
