using System.Security.Claims;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers.Admin;

[ApiController]
[Route("api/admin/manual-confirm")]
[Authorize(Roles = "ADMIN,STAFF")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class ManualConfirmController(ISmartLockerDbContext db, ILockerNotificationService? notifications = null) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() => Ok(await db.Bookings
        .Where(b => b.ManualConfirmPhotoUrl != null && b.Status == BookingStatus.STORED)
        .Select(b => new { b.Id, b.BookingCode, b.StationId, b.LockerId, b.CheckedOutAt }).ToListAsync());

    [HttpGet("{id:guid}/photo")]
    public async Task<IActionResult> Photo(Guid id)
    {
        var name = await db.Bookings.Where(b => b.Id == id).Select(b => b.ManualConfirmPhotoUrl).FirstOrDefaultAsync();
        if (name == null || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _)
            || Path.GetFileName(name) != name || Path.GetExtension(name) != ".jpg") return NotFound();
        var path = Path.Combine(AppContext.BaseDirectory, "App_Data", "manual-confirm", name);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return File(await System.IO.File.ReadAllBytesAsync(path), "image/jpeg");
    }

    public record ReviewRequest(string Reason);

    [HttpPost("{id:guid}/approve")]
    public Task<IActionResult> Approve(Guid id, ReviewRequest request) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        if (db.Database.IsRelational() && db is DbContext tracked) tracked.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
        var result = await ApproveCore(id, request);
        if (result is OkObjectResult && transaction != null) await transaction.CommitAsync();
        if (result is OkObjectResult && notifications != null)
        {
            var booking = await db.Bookings.Where(b => b.Id == id).Select(b => new { b.StationId, b.LockerId }).SingleAsync();
            try { await notifications.NotifyLockerStatusChangedAsync(booking.StationId, booking.LockerId!.Value); } catch { /* Polling recovers delivery failure after commit. */ }
        }
        return result;
    });

    private async Task<IActionResult> ApproveCore(Guid id, ReviewRequest request)
    {
        if (!Guid.TryParse(User.FindFirstValue("uid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var staffId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 500)
            return BadRequest(new { message = "Cần lý do xác nhận, tối đa 500 ký tự." });
        var booking = await db.Bookings.Include(b => b.Locker).Include(b => b.AccessCredential).FirstOrDefaultAsync(b => b.Id == id);
        if (booking == null || booking.Locker == null || booking.Status != BookingStatus.STORED
            || booking.CheckedOutAt == null || string.IsNullOrEmpty(booking.ManualConfirmPhotoUrl))
            return BadRequest(new { message = "Không có yêu cầu đang chờ duyệt." });
        var name = booking.ManualConfirmPhotoUrl;
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _)
            || Path.GetFileName(name) != name || Path.GetExtension(name) != ".jpg"
            || !System.IO.File.Exists(Path.Combine(AppContext.BaseDirectory, "App_Data", "manual-confirm", name)))
            return BadRequest(new { message = "Không đọc được ảnh bằng chứng. Chưa thể xác nhận đóng đơn." });
        if (await db.Bookings.AnyAsync(b => b.Id != id && b.LockerId == booking.LockerId
            && (b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED)))
            return Conflict(new { message = "Ô tủ đang liên quan đến đơn khác. Cần kiểm tra thực địa trước khi xử lý." });
        booking.Status = BookingStatus.COMPLETED_MANUAL;
        booking.CheckedOutAt = DateTime.UtcNow;
        booking.Locker.BusinessStatus = LockerBusinessStatus.MAINTENANCE;
        booking.Locker.HealthStatus = LockerHealthStatus.MAINTENANCE;
        if (booking.AccessCredential is { } credential)
        {
            credential.IsActive = false;
            credential.RevokedAt = DateTime.UtcNow;
            credential.RevokeReason = "Staff approved sensor exception";
        }
        db.AuditLogs.Add(new AuditLog { UserId = staffId, Action = "MANUAL_CHECKOUT_APPROVED",
            TargetEntity = "Booking", TargetId = id.ToString(), Details = request.Reason.Trim() });
        // The incident remains open until the physical sensor has been repaired.
        await db.SaveChangesAsync();
        return Ok(new { message = "Đã đóng đơn. Ô tủ cần được bảo trì cảm biến." });
    }
}
