namespace Application.Interfaces;

public interface IPaymentGatewayService
{
    /// <summary>
    /// Tạo link thanh toán. Trả về (checkoutUrl, paymentLinkId).
    /// OrderCode phải là số nguyên (PayOS yêu cầu long).
    /// </summary>
    Task<(string CheckoutUrl, string PaymentLinkId)> CreatePaymentLinkAsync(
        long orderCode, int amount, string description, string returnUrl, string cancelUrl);

    /// <summary>
    /// Xác thực webhook data từ PayOS. Trả về (orderCode, isSuccess).
    /// </summary>
    Task<(long OrderCode, bool IsSuccess)> VerifyWebhookAsync(object webhookBody);

    /// <summary>
    /// Lấy thông tin payment link theo orderCode.
    /// </summary>
    Task<object?> GetPaymentLinkInfoAsync(long orderCode);

    /// <summary>
    /// Hủy payment link.
    /// </summary>
    Task<bool> CancelPaymentLinkAsync(long orderCode, string? reason = null);
}
