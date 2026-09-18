namespace Application.DTOs.Booking;

public class CreateBookingRequest
{
    public Guid StationId { get; set; }
    public string Size { get; set; } = string.Empty;  // "S", "M", "L"
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
}
