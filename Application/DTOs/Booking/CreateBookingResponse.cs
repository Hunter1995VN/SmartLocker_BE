namespace Application.DTOs.Booking;

public class CreateBookingResponse
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string PaymentUrl { get; set; } = string.Empty;
    public DateTime PaymentExpiresAt { get; set; }
}
