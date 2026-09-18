namespace Domain.Entities;

using Domain.Enums;

public class Station
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public StationStatus Status { get; set; } = StationStatus.ACTIVE;
    public TimeOnly OpensAt { get; set; }
    public TimeOnly ClosesAt { get; set; }
    public string Timezone { get; set; } = "Asia/Ho_Chi_Minh";
    public short TotalS { get; set; }
    public short TotalM { get; set; }
    public short TotalL { get; set; }
    public string? ContactPhone { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public virtual ICollection<Locker> Lockers { get; set; } = new List<Locker>();
    public virtual ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public virtual ICollection<PricingPolicy> PricingPolicies { get; set; } = new List<PricingPolicy>();
}
