namespace Application.Interfaces;

using Application.DTOs.Admin;
using Application.DTOs.Common;

public interface IAdminDashboardService
{
    Task<ApiResponse<AdminDashboardStatsDto>> GetStatsAsync();
    Task<ApiResponse<List<RevenueChartItemDto>>> GetRevenueChartAsync(int days = 30);
    Task<ApiResponse<List<StationOccupancyItemDto>>> GetStationOccupanciesAsync();
}
