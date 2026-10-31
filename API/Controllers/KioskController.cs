namespace API.Controllers;

using Application.DTOs.Kiosk;
using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("kiosk")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/kiosk")]
public class KioskController : ControllerBase
{
    [HttpPost("pay-overdue")]
    public async Task<IActionResult> PayOverdue(KioskFeePaymentRequest request)
    {
        var result = await _kioskService.PayOverdueAsync(request);
        return result.Success ? Ok(result) : BadRequest(result);
    }
    [HttpPost("operation-status")]
    public async Task<IActionResult> OperationStatus(KioskOperationStatusRequest request)
    {
        var result = await _kioskService.GetOperationStatusAsync(request);
        return result.Success ? Ok(result) : BadRequest(result);
    }
    private readonly IKioskService _kioskService;
    private readonly ILogger<KioskController> _logger;

    public KioskController(IKioskService kioskService, ILogger<KioskController> logger)
    {
        _kioskService = kioskService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/kiosk/check-in: Xác thực mã QR / Access Code và cấp quyền gửi hành lý
    /// </summary>
    [HttpPost("check-in")]
    public async Task<IActionResult> CheckIn([FromBody] KioskCheckInRequest request)
    {
        var result = await _kioskService.CheckInAsync(request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// POST /api/kiosk/check-out: Xác thực mã QR / Access Code và mở tủ lấy hành lý
    /// </summary>
    [HttpPost("check-out")]
    public async Task<IActionResult> CheckOut([FromBody] KioskCheckOutRequest request)
    {
        var result = await _kioskService.CheckOutAsync(request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// POST /api/kiosk/manual-confirm: Khách xác nhận thủ công kèm ảnh chụp khi cảm biến cửa bị lỗi (UC-C27)
    /// </summary>
    [HttpPost("manual-confirm")]
    public async Task<IActionResult> ManualConfirm([FromBody] KioskManualConfirmRequest request)
    {
        var result = await _kioskService.ManualConfirmAsync(request);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// GET /api/kiosk/station/{stationId}/status: Lấy danh sách ô tủ & trạng thái của trạm phục vụ màn hình Kiosk
    /// </summary>
    [HttpGet("station/{stationId:guid}/status")]
    public async Task<IActionResult> GetStationStatus(Guid stationId)
    {
        var result = await _kioskService.GetStationStatusAsync(stationId);
        if (!result.Success)
            return NotFound(result);

        return Ok(result);
    }
}
