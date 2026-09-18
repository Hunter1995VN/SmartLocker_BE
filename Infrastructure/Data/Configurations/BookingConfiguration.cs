namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("Bookings");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.BookingCode).IsRequired().HasMaxLength(20);
        
        builder.Property(x => x.Size)
            .IsRequired()
            .HasMaxLength(1)
            .HasConversion(v => v.ToString(), v => Enum.Parse<LockerSize>(v));

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<BookingStatus>(v))
            .HasDefaultValue(BookingStatus.PENDING_PAYMENT);

        builder.Property(x => x.BaseAmount).HasPrecision(12, 2).HasDefaultValue(0);
        builder.Property(x => x.IsOverdue).HasDefaultValue(false);
        builder.Property(x => x.CancellationReason).HasMaxLength(500);
        builder.Property(x => x.CancelPolicyRate).HasPrecision(4, 2);
        builder.Property(x => x.CheckInPhotoUrl).HasMaxLength(1000);
        builder.Property(x => x.ManualConfirmPhotoUrl).HasMaxLength(1000);
        builder.Property(x => x.ExtensionCount).HasDefaultValue(0);

        builder.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasOne(b => b.User)
            .WithMany(u => u.Bookings)
            .HasForeignKey(b => b.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Station)
            .WithMany(s => s.Bookings)
            .HasForeignKey(b => b.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.Locker)
            .WithMany(l => l.Bookings)
            .HasForeignKey(b => b.LockerId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasOne(b => b.PricingPolicy)
            .WithMany()
            .HasForeignKey(b => b.PricingPolicyId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasOne(b => b.AccessCredential)
            .WithOne(ac => ac.Booking)
            .HasForeignKey<AccessCredential>(ac => ac.BookingId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}
