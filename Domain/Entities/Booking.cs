namespace Domain.Entities;

using Domain.Enums;

public class Booking
{
    public Guid Id { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public Guid StationId { get; set; }
    public Guid? LockerId { get; set; }
    public Guid? PricingPolicyId { get; set; }
    public LockerSize Size { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.PENDING_PAYMENT;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public DateTime? PaymentExpiresAt { get; set; }
    public decimal BaseAmount { get; set; }
    public bool IsOverdue { get; set; }
    public DateTime? OverdueSince { get; set; }
    public string? CancellationReason { get; set; }
    public decimal? CancelPolicyRate { get; set; }
    public string? CheckInPhotoUrl { get; set; }
    public string? ManualConfirmPhotoUrl { get; set; }
    public short ExtensionCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public virtual User User { get; set; } = null!;
    public virtual Station Station { get; set; } = null!;
    public virtual Locker? Locker { get; set; }
    public virtual PricingPolicy? PricingPolicy { get; set; }
    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public virtual ICollection<BookingExtension> Extensions { get; set; } = new List<BookingExtension>();
    public virtual AccessCredential? AccessCredential { get; set; }
}
