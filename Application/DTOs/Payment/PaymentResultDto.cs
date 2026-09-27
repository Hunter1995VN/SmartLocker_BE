namespace Application.DTOs.Payment;

public class PaymentResultDto
{
    public bool IsSuccess { get; set; }
    public Guid PaymentId { get; set; }
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string OrderCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Message { get; set; } = string.Empty;
}
