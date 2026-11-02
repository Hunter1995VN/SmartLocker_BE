namespace Application.DTOs.Booking;

public class BookingListItemDto
{
    public Guid Id { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string StationAddress { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? HoldExpiresAt { get; set; }
    public double DurationHours { get; set; }
    public string? LockerCode { get; set; }
    public string? AccessCode { get; set; }
    public decimal BaseAmount { get; set; }
    public decimal? HourlyRate { get; set; }
    public short? MaxHours { get; set; }
    public bool IsOverdue { get; set; }
    public DateTime? OverdueSince { get; set; }
    public int? OverdueHours { get; set; }
    public decimal? OverdueFeePerHour { get; set; }
    public decimal? CurrentOverdueFee { get; set; }
    public bool CanExtend { get; set; }
    public bool CanCancel { get; set; }
    public DateTime CreatedAt { get; set; }
}
