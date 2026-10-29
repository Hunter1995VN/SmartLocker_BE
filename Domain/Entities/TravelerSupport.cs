namespace Domain.Entities;

public class PickupDelegation
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string Contact { get; set; } = "";
    public string PinHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Booking Booking { get; set; } = null!;
}

public class StationReview
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public int Stars { get; set; }
    public string Comment { get; set; } = "";
    public string Criteria { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Booking Booking { get; set; } = null!;
}

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

public class WalkInSession
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string SecretHash { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public Booking Booking { get; set; } = null!;
}
