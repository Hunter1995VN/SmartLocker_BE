namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class BookingExtensionConfiguration : IEntityTypeConfiguration<BookingExtension>
{
    public void Configure(EntityTypeBuilder<BookingExtension> builder)
    {
        builder.ToTable("BookingExtensions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ExtendedAt).HasDefaultValueSql("GETUTCDATE()");

        builder.HasOne(be => be.Booking)
            .WithMany(b => b.Extensions)
            .HasForeignKey(be => be.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(be => be.Payment)
            .WithMany(p => p.BookingExtensions)
            .HasForeignKey(be => be.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
