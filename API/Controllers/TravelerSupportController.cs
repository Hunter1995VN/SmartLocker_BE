using System.Security.Claims;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController, Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/bookings/{bookingId:guid}/support")]
public class TravelerSupportController(ISmartLockerDbContext db) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirstValue("uid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private Task<Booking?> Owned(Guid id) => db.Bookings.FirstOrDefaultAsync(b => b.Id == id && b.UserId == UserId);
    public record ReviewRequest(int Stars, string? Comment, string[]? Criteria);

    [HttpGet]
    public async Task<IActionResult> Get(Guid bookingId)
    {
        if (await Owned(bookingId) == null) return NotFound();
        return Ok(new { review = await db.StationReviews.Where(x => x.BookingId == bookingId)
            .Select(x => new { x.Stars, x.Comment, x.Criteria }).FirstOrDefaultAsync() });
    }

    [HttpPost("review")]
    public async Task<IActionResult> Review(Guid bookingId, ReviewRequest request)
    {
        var booking = await Owned(bookingId);
        if (booking == null) return NotFound();
        var allowed = new[] { "CLEAN", "FAST", "EASY_TO_FIND" };
        if (booking.Status is not (BookingStatus.COMPLETED or BookingStatus.COMPLETED_MANUAL)
            || booking.CheckedOutAt == null || request.Stars is < 1 or > 5
            || (request.Comment?.Length ?? 0) > 1000 || (request.Criteria?.Length ?? 0) > 3
            || request.Criteria?.Any(c => !allowed.Contains(c)) == true)
            return BadRequest(new { message = "Đánh giá 1–5 sao sau khi lấy đồ, góp ý tối đa 1000 ký tự; tiêu chí CLEAN, FAST, EASY_TO_FIND." });
        if (await db.StationReviews.AnyAsync(x => x.BookingId == bookingId))
            return Conflict(new { message = "Đơn đã được đánh giá." });
        db.StationReviews.Add(new StationReview { Id = Guid.NewGuid(), BookingId = bookingId,
            Stars = request.Stars, Comment = request.Comment?.Trim() ?? "",
            Criteria = string.Join(',', request.Criteria?.Distinct() ?? []), CreatedAt = DateTime.UtcNow });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        { return Conflict(new { message = "Đơn đã được đánh giá. Hãy tải lại kết quả." }); }
        return Ok(new { message = "Đã ghi nhận đánh giá." });
    }
}
