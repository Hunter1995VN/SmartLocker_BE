namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class MaintenanceTicketConfiguration : IEntityTypeConfiguration<MaintenanceTicket>
{
    public void Configure(EntityTypeBuilder<MaintenanceTicket> builder)
    {
        builder.ToTable("MaintenanceTickets", tb => tb.HasTrigger("trg_MaintenanceTickets_UpdatedAt"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TicketCode).HasMaxLength(50);
        
        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(50)
            .HasConversion(v => v.ToString(), v => Enum.Parse<MaintenanceStatus>(v));

        builder.Property(x => x.Priority).HasMaxLength(50).HasDefaultValue("MEDIUM");
        builder.Property(x => x.IssueDescription).HasMaxLength(1000);
        builder.Property(x => x.ResolutionDetails).HasMaxLength(1000);

        builder.HasOne(x => x.Station)
            .WithMany()
            .HasForeignKey(x => x.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ReportedByUser)
            .WithMany()
            .HasForeignKey(x => x.ReportedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.AssignedStaff)
            .WithMany()
            .HasForeignKey(x => x.AssignedStaffId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
