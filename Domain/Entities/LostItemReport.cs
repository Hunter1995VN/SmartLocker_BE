namespace Domain.Entities;
public class LostItemReport
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string Description { get; set; } = "";
    public string? Photo { get; set; }
    public string Status { get; set; } = "OPEN";
    public string? StaffResponse { get; set; }
    public DateTime CreatedAt { get; set; }
    public Booking Booking { get; set; } = null!;
}
