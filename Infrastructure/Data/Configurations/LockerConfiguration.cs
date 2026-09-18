namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class LockerConfiguration : IEntityTypeConfiguration<Locker>
{
    public void Configure(EntityTypeBuilder<Locker> builder)
    {
        builder.ToTable("Lockers");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.LockerCode).IsRequired().HasMaxLength(20);
        
        builder.Property(x => x.Size)
            .IsRequired()
            .HasMaxLength(1)
            .HasConversion(v => v.ToString(), v => Enum.Parse<LockerSize>(v));

        builder.Property(x => x.BusinessStatus)
            .IsRequired()
            .HasMaxLength(15)
            .HasConversion(v => v.ToString(), v => Enum.Parse<LockerBusinessStatus>(v))
            .HasDefaultValue(LockerBusinessStatus.AVAILABLE);

        builder.Property(x => x.HealthStatus)
            .IsRequired()
            .HasMaxLength(15)
            .HasConversion(v => v.ToString(), v => Enum.Parse<LockerHealthStatus>(v))
            .HasDefaultValue(LockerHealthStatus.HEALTHY);

        builder.Property(x => x.DoorState)
            .IsRequired()
            .HasMaxLength(10)
            .HasDefaultValue("UNKNOWN");

        builder.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasOne(x => x.Station)
            .WithMany(s => s.Lockers)
            .HasForeignKey(x => x.StationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
