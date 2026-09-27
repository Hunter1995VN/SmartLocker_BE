namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AbandonedPropertyRecordConfiguration : IEntityTypeConfiguration<AbandonedPropertyRecord>
{
    public void Configure(EntityTypeBuilder<AbandonedPropertyRecord> builder)
    {
        builder.ToTable("AbandonedPropertyRecords", tb => tb.HasTrigger("trg_AbandonedProperty_UpdatedAt"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.RecordCode).HasMaxLength(50);
        
        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(50)
            .HasConversion(v => v.ToString(), v => Enum.Parse<AbandonedPropertyStatus>(v));

        builder.Property(x => x.DisposalAction).HasMaxLength(50);
        builder.Property(x => x.ItemDescription).HasMaxLength(1000);
        builder.Property(x => x.Notes).HasMaxLength(1000);

        builder.HasOne(x => x.Station)
            .WithMany()
            .HasForeignKey(x => x.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Locker)
            .WithMany()
            .HasForeignKey(x => x.LockerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Booking)
            .WithMany()
            .HasForeignKey(x => x.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.StaffWitness)
            .WithMany()
            .HasForeignKey(x => x.StaffWitnessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.AdminApproval)
            .WithMany()
            .HasForeignKey(x => x.AdminApprovalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
