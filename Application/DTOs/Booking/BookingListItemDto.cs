namespace Application.DTOs.Booking;

public class BookingListItemDto
{
    public Guid Id { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public decimal BaseAmount { get; set; }
    public bool IsOverdue { get; set; }
    public DateTime CreatedAt { get; set; }
}
