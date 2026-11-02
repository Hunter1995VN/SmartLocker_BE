namespace Application.Interfaces;

using Application.DTOs.Common;
using Application.DTOs.Report;

public interface IOccupancyAnalyticsService
{
    Task<ApiResponse<OccupancyTurnoverOverviewDto>> GetOccupancyTurnoverOverviewAsync(
        OccupancyTurnoverFilterRequest request);

    Task<ExportedReportFileDto> ExportOccupancyReportAsync(
        OccupancyTurnoverFilterRequest request,
        string format,
        Guid actorUserId,
        string? actorEmail,
        string? actorRole);
}
