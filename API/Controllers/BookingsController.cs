namespace API.Controllers;

using Application.DTOs.Booking;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BookingsController : ControllerBase
{
    private readonly IBookingService _bookingService;

    public BookingsController(IBookingService bookingService)
    {
        _bookingService = bookingService;
    }

    /// <summary>
    /// Lấy UserId từ JWT claim "sub" hoặc "uid"
    /// </summary>
    private Guid GetUserId()
    {
        // Quân's JwtTokenService đặt userId vào claim "sub" và "uid"
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");

        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    /// <summary>
    /// UC-T02: Tạo booking mới (Create Booking)
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateBooking([FromBody] CreateBookingRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng từ token" });

        var result = await _bookingService.CreateBookingAsync(userId, request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// UC-T05: Xem chi tiết booking
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetBooking(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng từ token" });

        var result = await _bookingService.GetBookingByIdAsync(userId, id);
        if (!result.Success)
            return NotFound(result);

        return Ok(result);
    }

    /// <summary>
    /// UC-T06: Danh sách booking của user (My Bookings)
    /// </summary>
    [HttpGet("my")]
    public async Task<IActionResult> GetMyBookings(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng từ token" });

        var result = await _bookingService.GetUserBookingsAsync(userId, status, page, pageSize);
        return Ok(result);
    }

    /// <summary>
    /// UC-T07: Gia hạn booking
    /// </summary>
    [HttpPost("{id:guid}/extend")]
    public async Task<IActionResult> ExtendBooking(Guid id, [FromBody] ExtendBookingRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng từ token" });

        var result = await _bookingService.ExtendBookingAsync(userId, id, request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// UC-T08: Hủy booking
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelBooking(Guid id, [FromBody] CancelBookingRequest request)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng từ token" });

        var result = await _bookingService.CancelBookingAsync(userId, id, request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// UC-T09: Thanh toán phụ phí quá hạn
    /// </summary>
    [HttpPost("{id:guid}/pay-overdue")]
    public async Task<IActionResult> PayOverdueFee(Guid id)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Unauthorized(new { message = "Không xác định được người dùng từ token" });

        var result = await _bookingService.PayOverdueFeeAsync(userId, id);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }
}
