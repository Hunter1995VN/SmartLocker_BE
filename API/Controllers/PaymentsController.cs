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
    /// </summary>
    [HttpGet("payos-return")]
    public async Task<IActionResult> PayOSReturn(
        [FromQuery] long orderCode,
        [FromQuery] string status = "")
    {
        var result = await _paymentService.ProcessPayOSReturnAsync(orderCode, status);

        // Trong production sẽ redirect tới frontend:
        // return Redirect($"http://localhost:5173/payment/result?bookingId={result.Data?.BookingId}&status={result.Data?.Status}");
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
