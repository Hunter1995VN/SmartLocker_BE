namespace Application.DTOs.Booking;

public class CreateBookingResponse
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public decimal OriginalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? AppliedVoucherCode { get; set; }
    public decimal Amount { get; set; }
    public string PaymentUrl { get; set; } = string.Empty;
    public DateTime PaymentExpiresAt { get; set; }
    public string? QrCode { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountName { get; set; }
    public string? Bin { get; set; }
    public long OrderCode { get; set; }
}
