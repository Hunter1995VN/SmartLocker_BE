using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public class StationReviewReportConfiguration : IEntityTypeConfiguration<StationReviewReport>
{
    public void Configure(EntityTypeBuilder<StationReviewReport> b)
    {
        b.ToTable("StationReviewReports");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.Property(x => x.ResolvedAt).IsConcurrencyToken();
        b.HasOne(x => x.Review).WithMany(x => x.Reports).HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ReporterId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ReviewId, x.ReporterId }).IsUnique();
    }
}
