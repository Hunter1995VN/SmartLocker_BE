namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.Action).IsRequired().HasMaxLength(100);
        builder.Property(x => x.TargetEntity).IsRequired().HasMaxLength(100);
        builder.Property(x => x.TargetId).HasMaxLength(100);
        builder.Property(x => x.UserEmail).HasMaxLength(255);
        builder.Property(x => x.UserRole).HasMaxLength(50);
        builder.Property(x => x.IpAddress).HasMaxLength(50);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

        // Ignore navigation to User to avoid foreign key conflicts if table doesn't have FK
        builder.Ignore(x => x.User);
    }
}
