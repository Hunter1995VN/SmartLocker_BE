namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-A03, UC-A04: Manage Stations & Pricing Policies
/// </summary>
[ApiController]
[Route("api/admin/stations")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminStationsController : ControllerBase
{
    private readonly IAdminStationLockerService _stationLockerService;

    public AdminStationsController(IAdminStationLockerService stationLockerService)
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
    /// GET /api/admin/stations — Danh sách tất cả trạm (bao gồm INACTIVE)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStations([FromQuery] string? search = null)
    {
        var result = await _stationLockerService.GetStationsAsync(search);
        return Ok(result);
    }

    /// <summary>
    /// GET /api/admin/stations/{id} — Chi tiết trạm + bảng giá + availability
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetStation(Guid id)
    {
        var result = await _stationLockerService.GetStationByIdAsync(id);
        if (!result.Success)
            return NotFound(result);
        return Ok(result);
    }

    /// <summary>
    /// POST /api/admin/stations — Tạo trạm mới kèm tự động sinh ô tủ + bảng giá
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> CreateStation([FromBody] CreateStationRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _stationLockerService.CreateStationAsync(request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PUT /api/admin/stations/{id} — Cập nhật thông tin trạm
    /// </summary>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdateStation(Guid id, [FromBody] UpdateStationRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _stationLockerService.UpdateStationAsync(id, request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// DELETE /api/admin/stations/{id} — Ngưng hoạt động trạm (soft delete)
    /// </summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> DeleteStation(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _stationLockerService.DeleteStationAsync(id, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }

    /// <summary>
    /// PUT /api/admin/stations/{stationId}/pricing — Cập nhật bảng giá theo cỡ tủ
    /// </summary>
    [HttpPut("{stationId:guid}/pricing")]
    [Authorize(Roles = "ADMIN")]
    public async Task<IActionResult> UpdatePricingPolicy(Guid stationId, [FromBody] UpdatePricingPolicyRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng" });

        var result = await _stationLockerService.UpdatePricingPolicyAsync(stationId, request, userId);
        if (!result.Success)
            return BadRequest(result);
        return Ok(result);
    }
}
