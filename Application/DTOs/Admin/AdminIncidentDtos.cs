namespace Application.DTOs.Admin;

using Domain.Enums;

public class CreateIncidentRequest
{
    public Guid LockerId { get; set; }
    public Guid StationId { get; set; }
    public Guid? BookingId { get; set; }
    public IncidentType Type { get; set; }
    public string Severity { get; set; } = "MEDIUM";
    public string Description { get; set; } = string.Empty;
}

public class RemoteUnlockRequest
{
    public string Reason { get; set; } = string.Empty; // Lý do cứu hộ khách hàng (VD: Khách quên mã, kẹt cửa)
}

public class IncidentDto
{
    public Guid Id { get; set; }
    public string IncidentCode { get; set; } = string.Empty;
    public Guid? BookingId { get; set; }
    public string? BookingCode { get; set; }
    public Guid LockerId { get; set; }
    public string LockerCode { get; set; } = string.Empty;
    public Guid StationId { get; set; }
    public string StationName { get; set; } = string.Empty;
    public string ReportedBy { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ResolutionNote { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public bool RemoteUnlocked { get; set; }
    public DateTime CreatedAt { get; set; }
}
