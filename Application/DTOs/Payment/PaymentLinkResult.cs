namespace Application.DTOs.Payment;

public class PaymentLinkResult
{
    public string CheckoutUrl { get; set; } = string.Empty;
    public string PaymentLinkId { get; set; } = string.Empty;
    public string? QrCode { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountName { get; set; }
    public string? Bin { get; set; }

    public void Deconstruct(out string checkoutUrl, out string paymentLinkId)
    {
        checkoutUrl = CheckoutUrl;
        paymentLinkId = PaymentLinkId;
    }
}
