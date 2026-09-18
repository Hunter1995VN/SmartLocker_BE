namespace Application.Interfaces;

using Application.DTOs.Common;
using Application.DTOs.Payment;

public interface IPaymentService
{
    /// <summary>
    /// Xử lý PayOS webhook callback (server-to-server)
    /// </summary>
    Task<ApiResponse<PaymentResultDto>> ProcessPayOSWebhookAsync(object webhookData);

    /// <summary>
    /// Xử lý PayOS return URL (browser redirect) — chỉ trả trạng thái hiện tại
    /// </summary>
    Task<ApiResponse<PaymentResultDto>> ProcessPayOSReturnAsync(long orderCode, string status);

    /// <summary>
    /// Lấy chi tiết payment theo ID
    /// </summary>
    Task<ApiResponse<PaymentDto>> GetPaymentByIdAsync(Guid paymentId);
}
