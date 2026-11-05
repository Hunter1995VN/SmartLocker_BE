using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Infrastructure.Data.Configurations;
public class WalkInSessionConfiguration : IEntityTypeConfiguration<WalkInSession> {
    public void Configure(EntityTypeBuilder<WalkInSession> b) {
        b.ToTable("WalkInSessions"); b.HasKey(x=>x.Id);
        b.Property(x=>x.SecretHash).HasMaxLength(255); b.Property(x=>x.ContactPhone).HasMaxLength(30);
        b.HasOne(x=>x.Booking).WithMany().HasForeignKey(x=>x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>x.BookingId).IsUnique();
    }
}
