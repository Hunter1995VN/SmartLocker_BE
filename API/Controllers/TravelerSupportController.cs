using System.Security.Claims;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Services;
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
    private Task<Booking?> Owned(Guid id) => db.Bookings.Include(b => b.Locker).FirstOrDefaultAsync(b => b.Id == id && b.UserId == UserId);
    public record ReviewRequest(int Stars, string? Comment, string[]? Criteria);
    public record LostRequest(string Description, string? Photo);

    [HttpGet]
    public async Task<IActionResult> Get(Guid bookingId)
    {
        if (await Owned(bookingId) == null) return NotFound();
        return Ok(new {
            review = await db.StationReviews.Where(x => x.BookingId == bookingId)
                .Select(x => new { x.Stars, x.Comment, x.Criteria }).FirstOrDefaultAsync(),
            lostReports = await db.LostItemReports.Where(x => x.BookingId == bookingId)
                .OrderByDescending(x => x.CreatedAt).Select(x => new { x.Id, x.Description, x.Status, x.StaffResponse }).ToListAsync(),
            notifications = await db.Notifications.Where(x => x.BookingId == bookingId && x.UserId == UserId
                && x.NotificationType == "GENERIC" && x.Title == "Phản hồi yêu cầu tìm đồ" && x.ReadAt == null)
                .OrderByDescending(x => x.CreatedAt).Take(20).Select(x => new { x.Id, x.Title, x.Body, x.CreatedAt }).ToListAsync()
        });
    }

    [HttpPost("notifications/{id:guid}/read")]
    public async Task<IActionResult> ReadNotification(Guid bookingId, Guid id)
    {
        if (await Owned(bookingId) == null) return NotFound();
        var item = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.BookingId == bookingId && x.UserId == UserId);
        if (item == null) return NotFound();
        item.ReadAt ??= DateTime.UtcNow; item.Status = "READ";
        await db.SaveChangesAsync(); return NoContent();
    }

    [HttpPost("lost-items")]
    public Task<IActionResult> ReportLost(Guid bookingId, LostRequest request) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        if (db.Database.IsRelational() && db is DbContext tracked) tracked.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
        var result = await ReportLostCore(bookingId, request);
        if (result is OkObjectResult && transaction != null) await transaction.CommitAsync();
        return result;
    });

    private async Task<IActionResult> ReportLostCore(Guid bookingId, LostRequest request)
    {
        var booking = await Owned(bookingId);
        if (booking == null) return NotFound();
        if (booking.Status is not (BookingStatus.COMPLETED or BookingStatus.COMPLETED_MANUAL)
            || booking.CheckedOutAt == null || booking.CheckedOutAt < DateTime.UtcNow.AddDays(-7)
            || string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 2000)
            return BadRequest(new { message = "Báo quên đồ trong vòng 7 ngày sau lấy đồ, mô tả tối đa 2000 ký tự." });
        if (request.Photo != null && !SupportInput.IsJpeg(request.Photo, 500000))
            return BadRequest(new { message = "Ảnh minh họa phải là JPEG, tối đa 500 KB." });
        if (await db.LostItemReports.AnyAsync(x => x.BookingId == bookingId && (x.Status == "OPEN" || x.Status == "FOUND")))
            return Conflict(new { message = "Đã có yêu cầu tìm đồ đang chờ xử lý hoặc bàn giao." });
        var report = new LostItemReport { Id = Guid.NewGuid(), BookingId = bookingId,
            Description = request.Description.Trim(), Photo = request.Photo, CreatedAt = DateTime.UtcNow };
        db.LostItemReports.Add(report);
        if (booking.LockerId.HasValue)
            db.SecurityIncidents.Add(new SecurityIncident { Id = Guid.NewGuid(), IncidentCode = "LF-" + report.Id.ToString("N")[..20],
                BookingId = booking.Id, LockerId = booking.LockerId.Value, StationId = booking.StationId,
                ReportedByUserId = UserId, Type = IncidentType.OTHER, Severity = "HIGH", Status = IncidentStatus.OPEN,
                Description = "Yêu cầu tìm đồ " + report.Id + ": " + report.Description[..Math.Min(report.Description.Length, 800)], CreatedAt = DateTime.UtcNow });
        if (booking.Locker is { BusinessStatus: LockerBusinessStatus.AVAILABLE } locker
            && !await db.Bookings.AnyAsync(b => b.Id != bookingId && b.LockerId == locker.Id
                && (b.Status == BookingStatus.PENDING_PAYMENT || b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED)))
        {
            locker.BusinessStatus = LockerBusinessStatus.BLOCKED;
            locker.Notes = "Lost item inspection: " + report.Id;
        }
        await db.SaveChangesAsync(); return Ok(new { report.Id, report.Status });
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
