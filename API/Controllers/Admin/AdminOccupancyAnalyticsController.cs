namespace API.Controllers.Admin;

using System.Security.Claims;
using Application.DTOs.Report;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// UC-A13: Phân tích tỷ lệ lấp đầy (Occupancy Rate) và tần suất xoay vòng ngăn tủ (Locker Turnover)
/// </summary>
[ApiController]
[Route("api/admin/occupancy-analytics")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminOccupancyAnalyticsController : ControllerBase
{
    private readonly IOccupancyAnalyticsService _occupancyService;

    public AdminOccupancyAnalyticsController(IOccupancyAnalyticsService occupancyService)
    {
        _occupancyService = occupancyService;
    }

    private Guid GetUserId()
    {
        var uid = User.FindFirstValue("uid")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
        return Guid.TryParse(uid, out var userId) ? userId : Guid.Empty;
    }

    private string? GetUserEmail() => User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
    private string? GetUserRole() => User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("role");

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? stationId = null,
        [FromQuery] string? lockerSize = null,
        [FromQuery] string granularity = "DAY")
    {
        var result = await _occupancyService.GetOccupancyTurnoverOverviewAsync(new OccupancyTurnoverFilterRequest
        {
            FromDate = fromDate,
            ToDate = toDate,
            StationId = stationId,
            LockerSize = lockerSize,
            Granularity = granularity
        });

        return Ok(result);
    }

    [HttpGet("export")]
    public async Task<IActionResult> ExportReport(
        [FromQuery] string format = "excel",
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? stationId = null,
        [FromQuery] string? lockerSize = null,
        [FromQuery] string granularity = "DAY")
    {
        var file = await _occupancyService.ExportOccupancyReportAsync(
            new OccupancyTurnoverFilterRequest
            {
                FromDate = fromDate,
                ToDate = toDate,
                StationId = stationId,
                LockerSize = lockerSize,
                Granularity = granularity
            },
            format,
            GetUserId(),
            GetUserEmail(),
            GetUserRole());

        return File(file.Content, file.ContentType, file.FileName);
    }
}
