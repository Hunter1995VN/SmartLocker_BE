using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Infrastructure.Data.Configurations;
public class PickupDelegationConfiguration : IEntityTypeConfiguration<PickupDelegation> {
    public void Configure(EntityTypeBuilder<PickupDelegation> b) {
        b.ToTable("PickupDelegations"); b.HasKey(x=>x.Id);
        b.Property(x=>x.Contact).HasMaxLength(255); b.Property(x=>x.PinHash).HasMaxLength(255);
        b.HasOne(x=>x.Booking).WithMany().HasForeignKey(x=>x.BookingId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x=>x.BookingId);
    }
}
