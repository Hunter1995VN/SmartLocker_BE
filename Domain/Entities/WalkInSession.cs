namespace Domain.Entities;
public class WalkInSession {
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string SecretHash { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public Booking Booking { get; set; } = null!;
}
