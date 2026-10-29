using System.Security.Claims;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers.Admin;

[ApiController, Authorize(Roles = "ADMIN,STAFF"), Route("api/admin/traveler-support")]
public class TravelerSupportAdminController(ISmartLockerDbContext db) : ControllerBase
{
    [HttpGet("lost-items")]
    public async Task<IActionResult> LostItems() => Ok(await db.LostItemReports.OrderByDescending(x => x.CreatedAt).Take(100)
        .Select(x => new { x.Id, x.BookingId, x.Booking.BookingCode, x.Booking.StationId, x.Booking.LockerId,
            x.Description, x.Photo, x.Status, x.StaffResponse, x.CreatedAt }).ToListAsync());
    public record ResolveRequest(string Status, string Response);
    [HttpPost("lost-items/{id:guid}")]
    public async Task<IActionResult> Resolve(Guid id, ResolveRequest request)
    {
        if (request.Status is not ("FOUND" or "NOT_FOUND" or "RETURNED") || string.IsNullOrWhiteSpace(request.Response) || request.Response.Length > 2000)
            return BadRequest(new { message = "Cần kết quả kiểm tra và phản hồi tối đa 2000 ký tự." });
        var report = await db.LostItemReports.Include(x => x.Booking).ThenInclude(b => b.Locker).FirstOrDefaultAsync(x => x.Id == id);
        if (report == null) return NotFound();
        report.Status = request.Status; report.StaffResponse = request.Response.Trim();
        var locker = report.Booking.Locker;
        if (locker?.BusinessStatus == LockerBusinessStatus.BLOCKED && locker.Notes == "Lost item inspection: " + id
            && locker.HealthStatus == LockerHealthStatus.HEALTHY)
        { locker.BusinessStatus = LockerBusinessStatus.AVAILABLE; locker.Notes = null; }
        Guid.TryParse(User.FindFirstValue("uid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId);
        db.AuditLogs.Add(new AuditLog { UserId = userId, Action = "LOST_ITEM_RESPONSE", TargetEntity = "LostItemReport", TargetId = id.ToString(), Details = request.Status + ": " + request.Response });
        await db.SaveChangesAsync(); return Ok(new { report.Status });
    }
    [HttpGet("ratings")]
    public async Task<IActionResult> Ratings() => Ok(await db.StationReviews.GroupBy(x => x.Booking.StationId)
        .Select(g => new { stationId = g.Key, count = g.Count(), average = g.Average(x => x.Stars) }).ToListAsync());
}
