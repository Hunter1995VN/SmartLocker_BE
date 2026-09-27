namespace Domain.Entities;

using Domain.Enums;

public class AbandonedPropertyRecord
{
    public Guid Id { get; set; }
    public string RecordCode { get; set; } = string.Empty;
    public Guid BookingId { get; set; }
    public Guid LockerId { get; set; }
    public Guid StationId { get; set; }
    public int OverdueHours { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? InventoryPhotosJson { get; set; } // JSON array URL ảnh kiểm kê
    public Guid StaffWitnessId { get; set; } // Nhân viên lập biên bản (Mắt 1)
    public Guid? AdminApprovalId { get; set; } // Admin phê duyệt (Mắt 2)
    public AbandonedPropertyStatus Status { get; set; } = AbandonedPropertyStatus.REPORTED;
    public string? DisposalAction { get; set; } // LIQUIDATED, STORED_IN_WAREHOUSE, RETURNED_TO_OWNER, DESTROYED
    public string? Notes { get; set; }
    public DateTime ReportedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }

    // Navigation
    public virtual Booking Booking { get; set; } = null!;
    public virtual Locker Locker { get; set; } = null!;
    public virtual Station Station { get; set; } = null!;
    public virtual User StaffWitness { get; set; } = null!;
    public virtual User? AdminApproval { get; set; }
}
