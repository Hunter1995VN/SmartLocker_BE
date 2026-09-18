namespace API.Controllers;

using Application.DTOs.Booking;
using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/[controller]")]
public class BookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public BookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    // Temporary: Get userId from header until JWT Auth is implemented
    private Guid GetUserId()
    {
        var userIdHeader = Request.Headers["X-User-Id"].FirstOrDefault();
        if (Guid.TryParse(userIdHeader, out var userId))
            return userId;
        return Guid.Empty; // Will be replaced with JWT claim
    }

    /// <summary>
    /// UC-T02: Tạo booking mới (Create Booking)
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateBooking([FromBody] CreateBookingRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Vui l\u00f2ng cung c\u1ea5p X-User-Id header" });

        var result = await _bookingService.CreateBookingAsync(userId, request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// UC-T05: Xem chi ti\u1ebft booking
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetBooking(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Vui l\u00f2ng cung c\u1ea5p X-User-Id header" });

        var result = await _bookingService.GetBookingByIdAsync(userId, id);
        if (!result.Success)
            return NotFound(result);

        return Ok(result);
    }

    /// <summary>
    /// UC-T06: Danh s\u00e1ch booking c\u1ee7a user
    /// </summary>
    [HttpGet("my")]
    public async Task<IActionResult> GetMyBookings(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Vui l\u00f2ng cung c\u1ea5p X-User-Id header" });

        var result = await _bookingService.GetUserBookingsAsync(userId, status, page, pageSize);
        return Ok(result);
    }

    /// <summary>
    /// UC-T07: Gia h\u1ea1n booking
    /// </summary>
    [HttpPost("{id:guid}/extend")]
    public async Task<IActionResult> ExtendBooking(Guid id, [FromBody] ExtendBookingRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Vui l\u00f2ng cung c\u1ea5p X-User-Id header" });

        var result = await _bookingService.ExtendBookingAsync(userId, id, request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// UC-T08: H\u1ee7y booking
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelBooking(Guid id, [FromBody] CancelBookingRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Vui l\u00f2ng cung c\u1ea5p X-User-Id header" });

        var result = await _bookingService.CancelBookingAsync(userId, id, request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// UC-T09: Thanh to\u00e1n ph\u1ee5 ph\u00ed qu\u00e1 h\u1ea1n
    /// </summary>
    [HttpPost("{id:guid}/pay-overdue")]
    public async Task<IActionResult> PayOverdueFee(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Vui l\u00f2ng cung c\u1ea5p X-User-Id header" });

        var result = await _bookingService.PayOverdueFeeAsync(userId, id);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }
}
