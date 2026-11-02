namespace Application.DTOs.Admin;

public class AdminRefundDto
{
    public Guid PaymentId { get; set; }
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string UserFullName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string UserPhoneNumber { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string LockerCode { get; set; } = string.Empty;
    public decimal OriginalAmount { get; set; }
    public decimal RefundAmount { get; set; }
    public string Status { get; set; } = string.Empty; // PENDING, PAID, FAILED, EXPIRED
    public string OrderCode { get; set; } = string.Empty;
    public string? CancellationReason { get; set; }
    public decimal? CancelPolicyRate { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

public class ProcessRefundRequest
{
    public string? Note { get; set; }
}

