namespace Application.DTOs.Admin;

public class AuditLogItemDto
{
    public long Id { get; set; }
    public Guid? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string UserRole { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string TargetEntity { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AuditLogQueryResponse
{
    public int TotalCount { get; set; }
    public int EmergencyUnlockCount { get; set; }
    public int PolicyAndStationChangeCount { get; set; }
    public int UserAndSecurityCount { get; set; }
    public int AbandonedPropertyCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<AuditLogItemDto> Items { get; set; } = new();
}
