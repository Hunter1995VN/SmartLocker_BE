namespace Domain.Entities;

public class BookingExtension
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public Guid PaymentId { get; set; }
    public DateTime OldEndAt { get; set; }
    public DateTime NewEndAt { get; set; }
    public DateTime ExtendedAt { get; set; }

    // Navigation
    public virtual Booking Booking { get; set; } = null!;
    public virtual Payment Payment { get; set; } = null!;
}
