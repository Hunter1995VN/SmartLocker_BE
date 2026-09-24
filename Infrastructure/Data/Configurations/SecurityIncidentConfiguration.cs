namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class SecurityIncidentConfiguration : IEntityTypeConfiguration<SecurityIncident>
{
    public void Configure(EntityTypeBuilder<SecurityIncident> builder)
    {
        builder.ToTable("SecurityIncidents", tb => tb.HasTrigger("trg_SecurityIncidents_UpdatedAt"));
        builder.HasKey(x => x.Id);

        builder.Property(x => x.IncidentCode).HasMaxLength(50);
        
        builder.Property(x => x.Type)
            .IsRequired()
            .HasMaxLength(50)
            .HasConversion(v => v.ToString(), v => Enum.Parse<IncidentType>(v));

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(50)
            .HasConversion(v => v.ToString(), v => Enum.Parse<IncidentStatus>(v));

        builder.Property(x => x.Severity).HasMaxLength(50).HasDefaultValue("MEDIUM");
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.ResolutionNote).HasMaxLength(1000);

        builder.HasOne(x => x.Station)
            .WithMany()
            .HasForeignKey(x => x.StationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Locker)
            .WithMany()
            .HasForeignKey(x => x.LockerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ReportedByUser)
            .WithMany()
            .HasForeignKey(x => x.ReportedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ResolvedByUser)
            .WithMany()
            .HasForeignKey(x => x.ResolvedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
