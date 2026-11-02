namespace Application.DTOs.Booking;

public class CancelPreviewDto
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool CanCancel { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RefundRate { get; set; }
    public decimal RefundAmount { get; set; }
    public int DailyCancellationCount { get; set; }
    public int DailyCancelledCountToday { get => DailyCancellationCount; set => DailyCancellationCount = value; }
    public bool IsDailyLimitExceeded { get; set; }
    public string PolicyTier { get; set; } = string.Empty; // FULL, DAILY_LIMIT_EXCEEDED, UNPAID, NOT_ELIGIBLE
    public string PolicyDescription { get; set; } = string.Empty;
    public string? ReasonIfBlocked { get; set; }
}
