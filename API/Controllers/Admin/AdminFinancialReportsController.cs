namespace API.Controllers.Admin;

using System.Security.Claims;
using Application.DTOs.Report;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// UC-A10: Xuất báo cáo doanh thu tài chính &amp; Lưu lượng giao dịch thanh toán
/// </summary>
[ApiController]
[Route("api/admin/financial-reports")]
[Authorize(Roles = "ADMIN,STAFF")]
public class AdminFinancialReportsController : ControllerBase
{
    private readonly IFinancialReportService _reportService;

    public AdminFinancialReportsController(IFinancialReportService reportService)
    {
        _reportService = reportService;
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
        [FromQuery] string? paymentKind = null,
        [FromQuery] string granularity = "DAY")
    {
        var result = await _reportService.GetFinancialReportAsync(new FinancialReportFilterRequest
        {
            FromDate = fromDate,
            ToDate = toDate,
            StationId = stationId,
            PaymentKind = paymentKind,
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
        [FromQuery] string? paymentKind = null,
        [FromQuery] string granularity = "DAY")
    {
        var file = await _reportService.ExportFinancialReportAsync(
            new FinancialReportFilterRequest
            {
                FromDate = fromDate,
                ToDate = toDate,
                StationId = stationId,
                PaymentKind = paymentKind,
                Granularity = granularity
            },
            format,
            GetUserId(),
            GetUserEmail(),
            GetUserRole());

        return File(file.Content, file.ContentType, file.FileName);
    }
}
