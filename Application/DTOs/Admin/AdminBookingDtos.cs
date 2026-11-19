namespace Application.DTOs.Admin;

public class AdminBookingListItemDto
{
    public Guid Id { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public decimal BaseAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public bool IsOverdue { get; set; }
    public DateTime? OverdueSince { get; set; }
    public short ExtensionCount { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Guid UserId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string? CustomerPhone { get; set; }

    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public Guid? LockerId { get; set; }
    public string? LockerCode { get; set; }
}

public class AdminBookingStatsDto
{
    public int TotalBookings { get; set; }
    public int PendingPaymentCount { get; set; }
    public int ConfirmedCount { get; set; }
    public int StoredCount { get; set; }
    public int CompletedCount { get; set; }
    public int CancelledCount { get; set; }
    public int OverdueCount { get; set; }
    public decimal TotalRevenue { get; set; }
}

public class AdminBookingPaymentDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string OrderCode { get; set; } = string.Empty;
    public string? Gateway { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AdminBookingExtensionDto
{
    public Guid Id { get; set; }
    public DateTime OldEndAt { get; set; }
    public DateTime NewEndAt { get; set; }
    public DateTime ExtendedAt { get; set; }
}

public class AdminBookingDetailDto : AdminBookingListItemDto
{
    public string? CancellationReason { get; set; }
    public string? AccessCode { get; set; }
    public List<AdminBookingPaymentDto> Payments { get; set; } = new();
    public List<AdminBookingExtensionDto> Extensions { get; set; } = new();
}

public class AdminCancelBookingRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class AdminCompleteBookingRequest
{
    public string? Note { get; set; }
}
