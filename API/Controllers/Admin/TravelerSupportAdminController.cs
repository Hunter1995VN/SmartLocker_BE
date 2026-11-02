using System.Security.Claims;
using Domain.Entities;
using Domain.Enums;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers.Admin;

[ApiController, Authorize(Roles = "ADMIN,STAFF"), Route("api/admin/traveler-support")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class TravelerSupportAdminController(ISmartLockerDbContext db) : ControllerBase
{
    [HttpGet("lost-items")]
    public async Task<IActionResult> LostItems() => Ok(await db.LostItemReports.OrderByDescending(x => x.CreatedAt).Take(100)
        .Select(x => new { x.Id, x.BookingId, x.Booking.BookingCode, x.Booking.StationId, x.Booking.LockerId,
            x.Description, x.Photo, x.Status, x.StaffResponse, x.CreatedAt }).ToListAsync());
    public record ResolveRequest(string Status, string Response);
    [HttpPost("lost-items/{id:guid}")]
    public Task<IActionResult> Resolve(Guid id, ResolveRequest request) => db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        if (db.Database.IsRelational() && db is DbContext tracked) tracked.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
        var result = await ResolveCore(id, request);
        if (result is OkObjectResult && transaction != null) await transaction.CommitAsync();
        return result;
    });

    private async Task<IActionResult> ResolveCore(Guid id, ResolveRequest request)
    {
        if (!Guid.TryParse(User.FindFirstValue("uid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        if (request.Status is not ("FOUND" or "NOT_FOUND" or "RETURNED") || string.IsNullOrWhiteSpace(request.Response) || request.Response.Length > 2000)
            return BadRequest(new { message = "Cần kết quả kiểm tra và phản hồi tối đa 2000 ký tự." });
        var report = await db.LostItemReports.Include(x => x.Booking).ThenInclude(b => b.Locker).FirstOrDefaultAsync(x => x.Id == id);
        if (report == null) return NotFound();
        if (report.Status == request.Status && report.StaffResponse == request.Response.Trim()) return Ok(new { report.Status });
        if (report.Status == "RETURNED") return Conflict(new { message = "Yêu cầu đã hoàn tất bàn giao đồ cho khách." });
        if (request.Status == "RETURNED" && report.Status != "FOUND") return Conflict(new { message = "Cần xác nhận đã tìm thấy trước khi bàn giao đồ." });
        report.Status = request.Status; report.StaffResponse = request.Response.Trim();
        var locker = report.Booking.Locker;
        if (locker?.BusinessStatus == LockerBusinessStatus.BLOCKED && locker.Notes == "Lost item inspection: " + id
            && locker.HealthStatus == LockerHealthStatus.HEALTHY
            && !await db.Bookings.AnyAsync(b => b.Id != report.BookingId && b.LockerId == locker.Id
                && (b.Status == BookingStatus.PENDING_PAYMENT || b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED)))
        { locker.BusinessStatus = LockerBusinessStatus.AVAILABLE; locker.Notes = null; }
        db.Notifications.Add(new Notification { Id = Guid.NewGuid(), UserId = report.Booking.UserId, BookingId = report.BookingId,
            Title = "Phản hồi yêu cầu tìm đồ", Body = request.Status + ": " + request.Response.Trim(), CreatedAt = DateTime.UtcNow });
        var incidentCode = "LF-" + report.Id.ToString("N")[..20];
        var incident = await db.SecurityIncidents.FirstOrDefaultAsync(x => x.BookingId == report.BookingId && x.IncidentCode == incidentCode);
        if (incident != null)
        {
            incident.Status = IncidentStatus.RESOLVED;
            incident.ResolvedByUserId = userId;
            incident.ResolvedAt = DateTime.UtcNow;
            incident.ResolutionNote = request.Response.Trim()[..Math.Min(request.Response.Trim().Length, 1000)];
        }
        db.AuditLogs.Add(new AuditLog { UserId = userId, Action = "LOST_ITEM_RESPONSE", TargetEntity = "LostItemReport", TargetId = id.ToString(), Details = request.Status + ": " + request.Response });
        await db.SaveChangesAsync(); return Ok(new { report.Status });
    }
    [HttpGet("ratings")]
    public async Task<IActionResult> Ratings() => Ok(await db.StationReviews
        .GroupBy(x => new { x.Booking.StationId, StationName = x.Booking.Station.Name })
        .Select(g => new { stationId = g.Key.StationId, stationName = g.Key.StationName,
            count = g.Count(), average = g.Average(x => x.Stars) }).ToListAsync());
}
