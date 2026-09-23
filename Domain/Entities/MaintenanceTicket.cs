namespace Domain.Entities;

using Domain.Enums;

public class MaintenanceTicket
{
    public Guid Id { get; set; }
    public string TicketCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public Guid? LockerId { get; set; }
    public Guid? IoTDeviceId { get; set; }
    public Guid ReportedByUserId { get; set; }
    public Guid? AssignedStaffId { get; set; }
    public string IssueDescription { get; set; } = string.Empty;
    public MaintenanceStatus Status { get; set; } = MaintenanceStatus.OPEN;
    public string Priority { get; set; } = "MEDIUM"; // LOW, MEDIUM, HIGH
    public string? ResolutionDetails { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual Station Station { get; set; } = null!;
    public virtual Locker? Locker { get; set; }
    public virtual IoTDevice? IoTDevice { get; set; }
    public virtual User ReportedByUser { get; set; } = null!;
    public virtual User? AssignedStaff { get; set; }
}
