namespace Application.Interfaces;

using Application.DTOs.Common;
using Application.DTOs.Report;

public interface IFinancialReportService
{
    Task<ApiResponse<FinancialReportOverviewDto>> GetFinancialReportAsync(
        FinancialReportFilterRequest filter);

    Task<ExportedReportFileDto> ExportFinancialReportAsync(
        FinancialReportFilterRequest filter,
        string format,
        Guid actorId,
        string? actorEmail,
        string? actorRole);
}
