using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;
public class StationReviewConfiguration : IEntityTypeConfiguration<StationReview>
{
    public void Configure(EntityTypeBuilder<StationReview> b)
    {
        b.ToTable("StationReviews", t => t.HasCheckConstraint("CK_StationReviews_Stars", "Stars BETWEEN 1 AND 5"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Comment).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Criteria).HasMaxLength(150).IsRequired();
        b.Property(x => x.IsHidden).HasDefaultValue(false).IsConcurrencyToken();
        b.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BookingId).IsUnique();
        b.HasIndex(x => new { x.IsHidden, x.CreatedAt, x.Id }).IsDescending(false, true, true);
    }
}
