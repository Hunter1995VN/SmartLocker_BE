namespace Domain.Entities;
public class Notification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? BookingId { get; set; }
    public string Channel { get; set; } = "PUSH";
    public string Status { get; set; } = "PENDING";
    public string NotificationType { get; set; } = "GENERIC";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? DeepLink { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public string? FailedReason { get; set; }
    public short RetryCount { get; set; }
    public DateTime CreatedAt { get; set; }
}
