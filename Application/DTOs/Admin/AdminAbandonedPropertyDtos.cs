namespace Application.DTOs.Admin;

public class CreateAbandonedPropertyRequest
{
    public Guid BookingId { get; set; }
    public Guid LockerId { get; set; }
    public Guid StationId { get; set; }
    public int OverdueHours { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public List<string>? PhotoUrls { get; set; }
    public string? Notes { get; set; }
}

public class ApproveAbandonedPropertyRequest
{
    public string Action { get; set; } = "STORED_IN_WAREHOUSE"; // STORED_IN_WAREHOUSE, LIQUIDATED, RETURNED_TO_OWNER
    public string? ApprovalNotes { get; set; }
}

public class AbandonedPropertyRecordDto
{
    public Guid Id { get; set; }
    public string RecordCode { get; set; } = string.Empty;
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid LockerId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public int OverdueHours { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public List<string>? Photos { get; set; }
    public string StaffWitnessName { get; set; } = string.Empty;
    public string? AdminApprovalName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? DisposalAction { get; set; }
    public string? Notes { get; set; }
    public DateTime ReportedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
