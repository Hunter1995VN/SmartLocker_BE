namespace Domain.Entities;

using Domain.Enums;

public class PricingPolicy
{
    public Guid Id { get; set; }
    public Guid StationId { get; set; }
    public LockerSize Size { get; set; }
    public decimal PricePerBlock { get; set; }
    public short BlockHours { get; set; } = 1;
    public decimal OverdueFeePerHour { get; set; }
    public short GracePeriodMinutes { get; set; } = 15;
    public short MinHours { get; set; } = 1;
    public short MaxHours { get; set; } = 72;
    public short NoShowWindowMinutes { get; set; } = 90;
    public short RefundCutoffHours { get; set; } = 2;
    public decimal RefundRateEarly { get; set; } = 1.00m;
    public decimal RefundRateLate { get; set; } = 0.50m;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public virtual Station Station { get; set; } = null!;
    public virtual User? Creator { get; set; }
}
