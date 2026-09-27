namespace API.Controllers.Admin;

using Application.DTOs.Common;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// UC-A02: Operations Dashboard — Bảng điều khiển thống kê thời gian thực
/// </summary>
[ApiController]
[Route("api/admin/dashboard")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminDashboardController : ControllerBase
{
    private readonly IAdminDashboardService _dashboardService;

    public AdminDashboardController(IAdminDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    /// <summary>
    /// GET /api/admin/dashboard/stats — Lấy tổng hợp KPI: doanh thu, tủ, trạm, sự cố
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var result = await _dashboardService.GetStatsAsync();
        return Ok(result);
    }

    /// <summary>
    /// GET /api/admin/dashboard/revenue-chart?days=30 — Biểu đồ doanh thu theo ngày
    /// </summary>
    [HttpGet("revenue-chart")]
    public async Task<IActionResult> GetRevenueChart([FromQuery] int days = 30)
    {
        var result = await _dashboardService.GetRevenueChartAsync(days);
        return Ok(result);
    }

    /// <summary>
    /// GET /api/admin/dashboard/station-occupancies — Tỷ lệ lấp đầy từng trạm
    /// </summary>
    [HttpGet("station-occupancies")]
    public async Task<IActionResult> GetStationOccupancies()
    {
        var result = await _dashboardService.GetStationOccupanciesAsync();
        return Ok(result);
    }
}
