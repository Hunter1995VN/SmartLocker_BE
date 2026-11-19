namespace API.Controllers.Admin;

using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[ApiController]
[Route("api/admin/bookings")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminBookingsController : ControllerBase
{
    private readonly IAdminBookingService _bookingService;

    public AdminBookingsController(IAdminBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> GetBookings(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] Guid? stationId = null,
        [FromQuery] bool? isOverdue = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await _bookingService.GetBookingsAsync(search, status, stationId, isOverdue, fromDate, toDate, page, pageSize);
        return Ok(result);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats([FromQuery] Guid? stationId = null)
    {
        var result = await _bookingService.GetBookingStatsAsync(stationId);
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetBookingDetail(Guid id)
    {
        var result = await _bookingService.GetBookingDetailAsync(id);
        if (!result.Success) return NotFound(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelBooking(Guid id, [FromBody] AdminCancelBookingRequest request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty) return Unauthorized();

        var result = await _bookingService.CancelBookingByAdminAsync(adminId, id, request.Reason);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    [HttpPost("{id:guid}/force-complete")]
    public async Task<IActionResult> ForceCompleteBooking(Guid id, [FromBody] AdminCompleteBookingRequest? request)
    {
        var adminId = GetUserId();
        if (adminId == Guid.Empty) return Unauthorized();

        var result = await _bookingService.ForceCompleteBookingByAdminAsync(adminId, id, request?.Note);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }
}
