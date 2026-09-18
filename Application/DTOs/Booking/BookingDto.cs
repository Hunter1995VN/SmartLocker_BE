namespace Application.DTOs.Booking;

using Application.DTOs.Payment;

public class BookingDto
{
    public Guid Id { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string StationAddress { get; set; } = string.Empty;
    public string? LockerCode { get; set; }
    public string Size { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public decimal BaseAmount { get; set; }
    public bool IsOverdue { get; set; }
    public DateTime? OverdueSince { get; set; }
    public short ExtensionCount { get; set; }
    public string? CancellationReason { get; set; }
    public decimal? CancelPolicyRate { get; set; }
    public DateTime CreatedAt { get; set; }

    // Related data
    public List<PaymentDto> Payments { get; set; } = new();

    // Derived fields (calculated at response time)
    public decimal? CurrentOverdueFee { get; set; }
    public decimal? EstimatedRefundAmount { get; set; }
    public bool CanExtend { get; set; }
    public bool CanCancel { get; set; }
}
