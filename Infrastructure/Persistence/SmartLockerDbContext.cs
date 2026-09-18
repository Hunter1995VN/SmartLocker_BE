using Microsoft.EntityFrameworkCore;
using Domain.Entities;
using Domain.Enums;

namespace Infrastructure.Persistence;

/// <summary>
/// DbContext chính của SmartLocker - ánh xạ với database db65218 trên SQL Server.
/// </summary>
public class SmartLockerDbContext : DbContext
{
    public SmartLockerDbContext(DbContextOptions<SmartLockerDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // === Ánh xạ bảng Users ===
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users", t => t.HasTrigger("trg_Users"));
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).HasDefaultValueSql("newsequentialid()");

            entity.Property(u => u.FullName).IsRequired().HasMaxLength(100);
            entity.Property(u => u.Email).IsRequired().HasMaxLength(255);
            entity.Property(u => u.Phone).IsRequired().HasMaxLength(20);
            entity.Property(u => u.PasswordHash).IsRequired().HasMaxLength(255);

            entity.Property(u => u.Role)
                .IsRequired()
                .HasMaxLength(20)
                .HasConversion(v => v.ToString(), v => Enum.Parse<UserRole>(v))
                .HasDefaultValue(UserRole.TRAVELER);

            entity.Property(u => u.Status)
                .IsRequired()
                .HasMaxLength(30)
                .HasConversion(v => v.ToString(), v => Enum.Parse<UserStatus>(v))
                .HasDefaultValue(UserStatus.PENDING_VERIFICATION);

            entity.Property(u => u.OverdueDebt).HasColumnType("decimal(12,2)").HasDefaultValue(0m);
            entity.Property(u => u.GoogleId).HasMaxLength(128);
            entity.Property(u => u.AvatarUrl).HasMaxLength(512);
            entity.Property(u => u.CreatedAt).HasDefaultValueSql("getutcdate()");
            entity.Property(u => u.UpdatedAt).HasDefaultValueSql("getutcdate()");

            // Index unique cho Email và Phone
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasIndex(u => u.Phone).IsUnique()
                  .HasFilter("[Phone] <> '' AND [Phone] IS NOT NULL");
        });


        // === Ánh xạ bảng OtpCodes (theo schema slms_schema.sql) ===
        modelBuilder.Entity<OtpCode>(entity =>
        {
            entity.ToTable("OtpCodes");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).HasDefaultValueSql("newsequentialid()");

            // Map tên property → tên column trong SQL
            entity.Property(o => o.Recipient).HasColumnName("Recipient").IsRequired().HasMaxLength(255);
            entity.Property(o => o.CodeHash).HasColumnName("CodeHash").IsRequired().HasMaxLength(255);
            entity.Property(o => o.Purpose).HasColumnName("Purpose").IsRequired().HasMaxLength(20);
            entity.Property(o => o.Attempts).HasColumnName("Attempts").HasDefaultValue((short)0);
            entity.Property(o => o.MaxAttempts).HasColumnName("MaxAttempts").HasDefaultValue((short)5);
            entity.Property(o => o.ResendCount).HasColumnName("ResendCount").HasDefaultValue((short)0);
            entity.Property(o => o.LockedUntil).HasColumnName("LockedUntil");
            entity.Property(o => o.ExpiresAt).HasColumnName("ExpiresAt").IsRequired();
            entity.Property(o => o.UsedAt).HasColumnName("UsedAt");
            entity.Property(o => o.IpAddress).HasColumnName("IpAddress").HasMaxLength(45);
            entity.Property(o => o.CreatedAt).HasColumnName("CreatedAt").HasDefaultValueSql("getutcdate()");

            // FK → Users
            entity.HasOne(o => o.User)
                  .WithMany()
                  .HasForeignKey(o => o.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Index cho query nhanh
            entity.HasIndex(o => new { o.Recipient, o.Purpose });
        });

    }
}
