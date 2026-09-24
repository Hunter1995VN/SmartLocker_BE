namespace Infrastructure.Data.Configurations;

using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users", tb => tb.HasTrigger("trg_Users_UpdatedAt"));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.FullName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Email).IsRequired().HasMaxLength(255);
        builder.Property(x => x.Phone).IsRequired().HasMaxLength(20);
        builder.Property(x => x.PasswordHash).IsRequired().HasMaxLength(255);

        builder.Property(x => x.Role)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<UserRole>(v))
            .HasDefaultValue(UserRole.TRAVELER);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(30)
            .HasConversion(v => v.ToString(), v => Enum.Parse<UserStatus>(v))
            .HasDefaultValue(UserStatus.PENDING_VERIFICATION);

        builder.Property(x => x.OverdueDebt)
            .HasPrecision(12, 2)
            .HasDefaultValue(0);

        // Từ module Auth (Quân): Google OAuth
        builder.Property(x => x.GoogleId).HasMaxLength(128);
        builder.Property(x => x.AvatarUrl).HasMaxLength(512);

        builder.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("GETUTCDATE()");
    }
}
