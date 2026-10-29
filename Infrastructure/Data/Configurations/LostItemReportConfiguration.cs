using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Infrastructure.Data.Configurations;
public class LostItemReportConfiguration : IEntityTypeConfiguration<LostItemReport>
{
    public void Configure(EntityTypeBuilder<LostItemReport> b)
    {
        b.ToTable("LostItemReports"); b.HasKey(x => x.Id);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.StaffResponse).HasMaxLength(2000);
        b.Property(x => x.Status).HasMaxLength(30);
        b.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BookingId);
    }
}
