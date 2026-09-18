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
    /// Lấy trạng thái thanh toán từ PayOS ("PAID", "PENDING", "CANCELLED", etc.).
    /// Dùng cho Background Service đối soát tránh sót đơn khi webhook bị lỗi.
    /// </summary>
    Task<string?> GetPaymentStatusAsync(long orderCode);

    /// <summary>
    /// Hủy payment link.
    /// </summary>
    Task<bool> CancelPaymentLinkAsync(long orderCode, string? reason = null);
}
