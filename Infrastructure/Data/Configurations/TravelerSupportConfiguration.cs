using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Infrastructure.Data.Configurations;

public class TravelerSupportConfiguration : IEntityTypeConfiguration<PickupDelegation>, IEntityTypeConfiguration<StationReview>,
    IEntityTypeConfiguration<LostItemReport>, IEntityTypeConfiguration<WalkInSession>
{
    public void Configure(EntityTypeBuilder<PickupDelegation> b)
    {
        b.ToTable("PickupDelegations"); b.HasKey(x => x.Id);
        b.Property(x => x.Contact).HasMaxLength(255); b.Property(x => x.PinHash).HasMaxLength(255);
        b.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BookingId);
    }
    public void Configure(EntityTypeBuilder<StationReview> b)
    {
        b.ToTable("StationReviews"); b.HasKey(x => x.Id);
        b.Property(x => x.Comment).HasMaxLength(1000); b.Property(x => x.Criteria).HasMaxLength(150);
        b.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BookingId).IsUnique();
    }
    public void Configure(EntityTypeBuilder<LostItemReport> b)
    {
        b.ToTable("LostItemReports"); b.HasKey(x => x.Id);
        b.Property(x => x.Description).HasMaxLength(2000); b.Property(x => x.StaffResponse).HasMaxLength(2000);
        b.Property(x => x.Status).HasMaxLength(30);
        b.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BookingId);
    }
    public void Configure(EntityTypeBuilder<WalkInSession> b)
    {
        b.ToTable("WalkInSessions"); b.HasKey(x => x.Id);
        b.Property(x => x.SecretHash).HasMaxLength(255); b.Property(x => x.ContactPhone).HasMaxLength(30);
        b.HasOne(x => x.Booking).WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BookingId).IsUnique();
    }
}
