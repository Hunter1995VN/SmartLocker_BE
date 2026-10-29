namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class VoucherConfiguration : IEntityTypeConfiguration<Voucher>
{
    public void Configure(EntityTypeBuilder<Voucher> builder)
    {
        builder.ToTable("Vouchers");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Code)
            .IsRequired()
            .HasMaxLength(40);

        builder.HasIndex(x => x.Code).IsUnique();

        builder.Property(x => x.Title)
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(x => x.Description)
            .HasMaxLength(350);

        builder.Property(x => x.DiscountType)
            .IsRequired()
            .HasMaxLength(20)
            .HasDefaultValue("PERCENTAGE");

        builder.Property(x => x.DiscountValue)
            .HasPrecision(12, 2);

        builder.Property(x => x.MaxDiscountAmount)
            .HasPrecision(12, 2);

        builder.Property(x => x.MinOrderAmount)
            .HasPrecision(12, 2)
            .HasDefaultValue(0m);

        builder.Property(x => x.UsageLimit)
            .HasDefaultValue(1000);

        builder.Property(x => x.UsedCount)
            .HasDefaultValue(0);

        builder.Property(x => x.IsActive)
            .HasDefaultValue(true);

        builder.Property(x => x.CreatedAt)
            .HasDefaultValueSql("GETUTCDATE()");

        builder.Property(x => x.UpdatedAt)
            .HasDefaultValueSql("GETUTCDATE()");
    }
}
