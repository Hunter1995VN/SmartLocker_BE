namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AccessCredentialConfiguration : IEntityTypeConfiguration<AccessCredential>
{
    public void Configure(EntityTypeBuilder<AccessCredential> builder)
    {
        builder.ToTable("AccessCredentials", tb => tb.UseSqlOutputClause(false));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.QrNonce).IsRequired().HasMaxLength(64);
        builder.Property(x => x.AccessCodeHash).IsRequired().HasMaxLength(255);
        builder.Property(x => x.OfflineNonce).HasMaxLength(64);
        builder.Property(x => x.AuthFailedAttempts).HasDefaultValue(0);
        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.Property(x => x.IssuedAt).HasDefaultValueSql("GETUTCDATE()");
        builder.Property(x => x.RevokeReason).HasMaxLength(50);
        
        // FK is configured in BookingConfiguration
    }
}
