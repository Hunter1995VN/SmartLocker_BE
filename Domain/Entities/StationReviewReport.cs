namespace Domain.Entities;

public class StationReviewReport
{
    public Guid Id { get; set; }
    public Guid ReviewId { get; set; }
    public Guid ReporterId { get; set; }
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public StationReview Review { get; set; } = null!;
}
