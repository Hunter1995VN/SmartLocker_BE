namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PricingPolicyConfiguration : IEntityTypeConfiguration<PricingPolicy>
{
    public void Configure(EntityTypeBuilder<PricingPolicy> builder)
    {
        builder.ToTable("PricingPolicies");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Size)
            .IsRequired()
            .HasMaxLength(1)
            .HasConversion(v => v.ToString(), v => Enum.Parse<LockerSize>(v));

        builder.Property(x => x.PricePerBlock).HasPrecision(10, 2);
        builder.Property(x => x.BlockHours).HasDefaultValue(1);
        builder.Property(x => x.OverdueFeePerHour).HasPrecision(10, 2).HasDefaultValue(0);
        builder.Property(x => x.GracePeriodMinutes).HasDefaultValue(15);
        builder.Property(x => x.MinHours).HasDefaultValue(1);
        builder.Property(x => x.MaxHours).HasDefaultValue(72);
        builder.Property(x => x.NoShowWindowMinutes).HasDefaultValue(90);
        builder.Property(x => x.RefundCutoffHours).HasDefaultValue(2);
        builder.Property(x => x.RefundRateEarly).HasPrecision(4, 2).HasDefaultValue(1.00m);
        builder.Property(x => x.RefundRateLate).HasPrecision(4, 2).HasDefaultValue(0.50m);
        
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasOne(x => x.Station)
            .WithMany(s => s.PricingPolicies)
            .HasForeignKey(x => x.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Creator)
            .WithMany()
            .HasForeignKey(x => x.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}
