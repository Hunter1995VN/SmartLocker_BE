namespace Domain.Entities;

public class StationReview
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public int Stars { get; set; }
    public string Comment { get; set; } = "";
    public string Criteria { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsHidden { get; set; }
    public Booking Booking { get; set; } = null!;
    public ICollection<StationReviewReport> Reports { get; set; } = [];
}
