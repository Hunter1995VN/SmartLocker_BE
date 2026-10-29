namespace Application.DTOs.Admin;

public class CreateAbandonedPropertyRequest
{
    public Guid BookingId { get; set; }
    public Guid LockerId { get; set; }
    public Guid StationId { get; set; }
    public int OverdueHours { get; set; } = 72;
    public string ItemDescription { get; set; } = string.Empty;
    public List<string>? PhotoUrls { get; set; }
    public string? Notes { get; set; }
}

public class ApproveAbandonedPropertyRequest
{
    public string Action { get; set; } = "STORED_IN_WAREHOUSE"; // STORED_IN_WAREHOUSE, LIQUIDATED, RETURNED_TO_OWNER
    public string? ApprovalNotes { get; set; }
}

public class RejectAbandonedPropertyRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class ResolveAbandonedPropertyDetailRequest
{
    public string DisposalAction { get; set; } = string.Empty; // RETURNED_TO_OWNER, LIQUIDATED, STORED_IN_WAREHOUSE
    public string? Notes { get; set; }
    public string? ReceiverName { get; set; }
    public string? ReceiverIdNumber { get; set; } // CCCD / Passport khi bàn giao đồ thất lạc (UC-S11)
    public string? HandoverProofUrl { get; set; }
    public bool CollectOverdueDebt { get; set; } = true;
}

public class OverdueBookingCandidateDto
{
    public Guid BookingId { get; set; }
    public string BookingCode { get; set; } = string.Empty;
    public Guid LockerId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public decimal OverdueDebt { get; set; }
    public int OverdueHours { get; set; }
    public DateTime EndAt { get; set; }
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
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public decimal OverdueDebt { get; set; }
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
