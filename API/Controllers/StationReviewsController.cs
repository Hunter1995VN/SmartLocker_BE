using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController, Route("api/station-reviews")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class StationReviewsController(ISmartLockerDbContext db) : ControllerBase
{
    private const int PageSize = 3;
    private Guid? ViewerId => Guid.TryParse(User.FindFirstValue("uid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && id != Guid.Empty ? id : null;

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> List(CancellationToken ct, [Range(1, 1_000_000)] int page = 1)
    {
        var viewerId = ViewerId;
        var visible = db.StationReviews.AsNoTracking().Where(x => !x.IsHidden);
        var summary = await visible.GroupBy(_ => 1)
            .Select(g => new { Average = g.Average(x => x.Stars), Count = g.Count() })
            .SingleOrDefaultAsync(ct);
        var items = await visible.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(x => new
            {
                x.Id, x.Stars, x.Comment, x.Criteria, CreatedAt = DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc),
                StationName = x.Booking.Station.Name,
                LockerSize = x.Booking.Size,
                IsOwn = x.Booking.UserId == viewerId,
                HasReported = viewerId != null && x.Reports.Any(r => r.ReporterId == viewerId)
            }).ToListAsync(ct);
        return Ok(new { Items = items, Count = summary?.Count ?? 0, Average = summary?.Average ?? 0, PageSize });
    }

    public record ReportRequest(string? Reason);

    [HttpPost("{id:guid}/reports"), Authorize]
    public async Task<IActionResult> Report(Guid id, ReportRequest request, CancellationToken ct)
    {
        if (ViewerId is not Guid reporterId) return Unauthorized();
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500)
            return BadRequest(new { message = "Nhập lý do báo cáo, tối đa 500 ký tự." });

        var review = await db.StationReviews.AsNoTracking().Where(x => x.Id == id && !x.IsHidden)
            .Select(x => new { x.Booking.UserId }).SingleOrDefaultAsync(ct);
        if (review == null) return NotFound(new { message = "Đánh giá hiện không còn hiển thị." });
        if (review.UserId == reporterId)
            return BadRequest(new { message = "Bạn chỉ có thể báo cáo đánh giá của người khác." });
        if (await db.StationReviewReports.AnyAsync(x => x.ReviewId == id && x.ReporterId == reporterId, ct))
            return NoContent();

        db.StationReviewReports.Add(new StationReviewReport
        {
            Id = Guid.NewGuid(), ReviewId = id, ReporterId = reporterId, Reason = reason, CreatedAt = DateTime.UtcNow
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // A concurrent request from the same account already saved the report.
        }
        return NoContent();
    }
}
