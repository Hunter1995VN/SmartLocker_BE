using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers.Admin;

[ApiController, Authorize(Policy = "AdminOnly"), Route("api/admin/station-reviews")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class AdminStationReviewsController(ISmartLockerDbContext db) : ControllerBase
{
    private const int PageSize = 12;
    private Guid? AdminId => Guid.TryParse(User.FindFirstValue("uid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id != Guid.Empty ? id : null;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct, string status = "all", [Range(1, 1_000_000)] int page = 1)
    {
        var reviews = db.StationReviews.AsNoTracking();
        switch (status)
        {
            case "all": break;
            case "reported": reviews = reviews.Where(x => x.Reports.Any(r => r.ResolvedAt == null)); break;
            case "visible": reviews = reviews.Where(x => !x.IsHidden); break;
            case "hidden": reviews = reviews.Where(x => x.IsHidden); break;
            default: return BadRequest(new { message = "Bộ lọc đánh giá không hợp lệ." });
        }

        var count = await reviews.CountAsync(ct);
        var items = await reviews.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(x => new
            {
                x.Id, x.Stars, x.Comment, x.Criteria, x.IsHidden, CreatedAt = DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc),
                x.Booking.BookingCode, StationName = x.Booking.Station.Name, LockerSize = x.Booking.Size,
                Reports = x.Reports.Count, PendingReports = x.Reports.Count(r => r.ResolvedAt == null)
            }).ToListAsync(ct);
        return Ok(new { Items = items, Count = count, PageSize });
    }

    [HttpGet("{id:guid}/reports")]
    public async Task<IActionResult> Reports(Guid id, CancellationToken ct, [Range(1, 1_000_000)] int page = 1)
    {
        var reports = db.StationReviewReports.AsNoTracking().Where(x => x.ReviewId == id);
        var count = await reports.CountAsync(ct);
        var items = await reports.OrderBy(x => x.ResolvedAt != null).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(x => new
            {
                x.Id, x.Reason, CreatedAt = DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc), x.ResolvedAt
            }).ToListAsync(ct);
        return Ok(new { Items = items, Count = count, PageSize });
    }

    public record VisibilityRequest([Required] bool? IsHidden);

    [HttpPut("{id:guid}/visibility")]
    public async Task<IActionResult> SetVisibility(Guid id, VisibilityRequest request, CancellationToken ct)
    {
        if (AdminId is not Guid adminId) return Unauthorized();
        if (request.IsHidden is not bool isHidden) return BadRequest();
        var review = await db.StationReviews.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (review == null) return NotFound();
        if (review.IsHidden == isHidden) return NoContent();

        review.IsHidden = isHidden;
        Audit(adminId, isHidden ? "STATION_REVIEW_HIDE" : "STATION_REVIEW_RESTORE", nameof(StationReview), id);
        return await Save(ct);
    }

    [HttpPut("{id:guid}/reports/{reportId:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, Guid reportId, CancellationToken ct)
    {
        if (AdminId is not Guid adminId) return Unauthorized();
        var report = await db.StationReviewReports.SingleOrDefaultAsync(x => x.Id == reportId && x.ReviewId == id, ct);
        if (report == null) return NotFound();
        if (report.ResolvedAt != null) return NoContent();

        report.ResolvedAt = DateTime.UtcNow;
        Audit(adminId, "STATION_REVIEW_REPORT_RESOLVE", nameof(StationReviewReport), reportId);
        return await Save(ct);
    }

    private void Audit(Guid adminId, string action, string entity, Guid id) => db.AuditLogs.Add(new AuditLog
    {
        UserId = adminId, UserRole = "ADMIN", Action = action, TargetEntity = entity, TargetId = id.ToString()
    });

    private async Task<IActionResult> Save(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "Dữ liệu vừa được cập nhật. Hãy tải lại và kiểm tra." });
        }
        return NoContent();
    }
}
