namespace Domain.Entities;
public class PickupDelegation {
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string Contact { get; set; } = "";
    public string PinHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Booking Booking { get; set; } = null!;
}
