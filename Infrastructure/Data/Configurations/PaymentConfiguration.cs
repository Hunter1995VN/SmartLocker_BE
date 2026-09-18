namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("Payments");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Kind)
            .IsRequired()
            .HasMaxLength(10)
            .HasConversion(v => v.ToString(), v => Enum.Parse<PaymentKind>(v));

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(10)
            .HasConversion(v => v.ToString(), v => Enum.Parse<PaymentStatus>(v))
            .HasDefaultValue(PaymentStatus.PENDING);

        builder.Property(x => x.Amount).HasPrecision(12, 2);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3).HasDefaultValueSql("'VND'");
        builder.Property(x => x.OrderCode).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Gateway).HasMaxLength(20);
        builder.Property(x => x.GatewayTxnId).HasMaxLength(255);
        builder.Property(x => x.PaymentLink).HasMaxLength(2000);
        builder.Property(x => x.RefundAmount).HasPrecision(12, 2);

        builder.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasOne(p => p.Booking)
            .WithMany(b => b.Payments)
            .HasForeignKey(p => p.BookingId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
