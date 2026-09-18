namespace Domain.Entities;

using Domain.Enums;

public class Payment
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public PaymentKind Kind { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.PENDING;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "VND";
    public string OrderCode { get; set; } = string.Empty;
    public string? Gateway { get; set; }
    public string? GatewayTxnId { get; set; }
    public string? PaymentLink { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public decimal? RefundAmount { get; set; }
    public DateTime? RefundedAt { get; set; }
    public DateTime? WebhookReceivedAt { get; set; }
    public string? WebhookPayload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public virtual Booking Booking { get; set; } = null!;
    public virtual ICollection<BookingExtension> BookingExtensions { get; set; } = new List<BookingExtension>();
}
