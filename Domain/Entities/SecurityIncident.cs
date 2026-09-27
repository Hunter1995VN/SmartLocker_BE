namespace Domain.Entities;

using Domain.Enums;

public class SecurityIncident
{
    public Guid Id { get; set; }
    public string IncidentCode { get; set; } = string.Empty;
    public Guid? BookingId { get; set; }
    public Guid LockerId { get; set; }
    public Guid StationId { get; set; }
    public Guid ReportedByUserId { get; set; }
    public IncidentType Type { get; set; } = IncidentType.DOOR_JAMMED;
    public IncidentStatus Status { get; set; } = IncidentStatus.OPEN;
    public string Severity { get; set; } = "MEDIUM"; // LOW, MEDIUM, HIGH, CRITICAL
    public string Description { get; set; } = string.Empty;
    public string? ResolutionNote { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public bool RemoteUnlocked { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public virtual Booking? Booking { get; set; }
    public virtual Locker Locker { get; set; } = null!;
    public virtual Station Station { get; set; } = null!;
    public virtual User ReportedByUser { get; set; } = null!;
    public virtual User? ResolvedByUser { get; set; }
}
