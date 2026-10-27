namespace API.Controllers;

using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using PayOS.Models.Webhooks;

[ApiController]
[Route("api/[controller]")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _paymentService;

    public PaymentsController(IPaymentService paymentService)
    {
        _paymentService = paymentService;
    }

    /// <summary>
    /// PayOS Webhook callback (server-to-server) — gọi tự động bởi PayOS khi thanh toán xong
    /// </summary>
    [HttpPost("payos-webhook")]
    public async Task<IActionResult> PayOSWebhook([FromBody] Webhook webhookData)
    {
        var result = await _paymentService.ProcessPayOSWebhookAsync(webhookData);

        // PayOS yêu cầu trả về 200 OK để xác nhận đã nhận webhook
        return Ok(result);
    }

    /// <summary>
    /// PayOS Return URL callback (browser redirect) — user quay về sau khi thanh toán
    /// Tự động đối soát và kích hoạt đơn đặt tủ ngay lập tức
    /// </summary>
    [HttpGet("payos-return")]
    public async Task<IActionResult> PayOSReturn(
        [FromQuery] long orderCode,
        [FromQuery] string status = "")
    {
        var result = await _paymentService.SyncPaymentStatusAsync(orderCode);
        return Ok(result);
    }

    /// <summary>
    /// Chủ động đối soát & đồng bộ trạng thái thanh toán từ PayOS
    /// </summary>
    [HttpPost("sync/{orderCode:long}")]
    [HttpGet("sync/{orderCode:long}")]
    public async Task<IActionResult> SyncPayment(long orderCode)
    {
        var result = await _paymentService.SyncPaymentStatusAsync(orderCode);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    /// <summary>
    /// Xem chi tiết payment
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetPayment(Guid id)
    {
        var result = await _paymentService.GetPaymentByIdAsync(id);
        if (!result.Success)
            return NotFound(result);

        return Ok(result);
    }
}
