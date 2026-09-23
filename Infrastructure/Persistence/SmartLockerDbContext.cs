using Microsoft.EntityFrameworkCore;
using Domain.Entities;
using Domain.Enums;
using Application.Interfaces;

namespace Infrastructure.Persistence;

/// <summary>
/// DbContext chính của SmartLocker - ánh xạ với database db65218 trên SQL Server.
/// </summary>
public class SmartLockerDbContext : DbContext, ISmartLockerDbContext
{
    public SmartLockerDbContext(DbContextOptions<SmartLockerDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<Locker> Lockers => Set<Locker>();
    public DbSet<PricingPolicy> PricingPolicies => Set<PricingPolicy>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<BookingExtension> BookingExtensions => Set<BookingExtension>();
    public DbSet<AccessCredential> AccessCredentials => Set<AccessCredential>();
    public DbSet<IoTDevice> IoTDevices => Set<IoTDevice>();
    public DbSet<SecurityIncident> SecurityIncidents => Set<SecurityIncident>();
    public DbSet<MaintenanceTicket> MaintenanceTickets => Set<MaintenanceTicket>();
    public DbSet<AbandonedPropertyRecord> AbandonedPropertyRecords => Set<AbandonedPropertyRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // === Ánh xạ bảng Users ===
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
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

            entity.HasIndex(u => u.Email).IsUnique();
        });

        // === Ánh xạ bảng OtpCodes ===
        modelBuilder.Entity<OtpCode>(entity =>
        {
            entity.ToTable("OtpCodes");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(o => o.Recipient).IsRequired().HasMaxLength(255);
            entity.Property(o => o.CodeHash).IsRequired().HasMaxLength(255);
            entity.Property(o => o.Purpose).IsRequired().HasMaxLength(20);
        });

        // === Ánh xạ bảng Stations ===
        modelBuilder.Entity<Station>(entity =>
        {
            entity.ToTable("Stations");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(s => s.Name).IsRequired().HasMaxLength(150);
            entity.Property(s => s.Address).IsRequired().HasMaxLength(255);
            entity.Property(s => s.Latitude).HasColumnType("decimal(10,8)");
            entity.Property(s => s.Longitude).HasColumnType("decimal(11,8)");
            entity.Property(s => s.Status)
                .HasConversion(v => v.ToString(), v => Enum.Parse<StationStatus>(v));
        });

        // === Ánh xạ bảng Lockers ===
        modelBuilder.Entity<Locker>(entity =>
        {
            entity.ToTable("Lockers");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(l => l.LockerCode).IsRequired().HasMaxLength(50);
            entity.Property(l => l.Size).HasConversion(v => v.ToString(), v => Enum.Parse<LockerSize>(v));
            entity.Property(l => l.BusinessStatus).HasConversion(v => v.ToString(), v => Enum.Parse<LockerBusinessStatus>(v));
            entity.Property(l => l.HealthStatus).HasConversion(v => v.ToString(), v => Enum.Parse<LockerHealthStatus>(v));

            entity.HasOne(l => l.Station)
                .WithMany(s => s.Lockers)
                .HasForeignKey(l => l.StationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // === Ánh xạ bảng PricingPolicies ===
        modelBuilder.Entity<PricingPolicy>(entity =>
        {
            entity.ToTable("PricingPolicies");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(p => p.PricePerBlock).HasColumnType("decimal(12,2)");
            entity.Property(p => p.OverdueFeePerHour).HasColumnType("decimal(12,2)");
            entity.Property(p => p.Size).HasConversion(v => v.ToString(), v => Enum.Parse<LockerSize>(v));
        });

        // === Ánh xạ bảng Bookings ===
        modelBuilder.Entity<Booking>(entity =>
        {
            entity.ToTable("Bookings");
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(b => b.BookingCode).IsRequired().HasMaxLength(50);
            entity.Property(b => b.BaseAmount).HasColumnType("decimal(12,2)");
            entity.Property(b => b.Size).HasConversion(v => v.ToString(), v => Enum.Parse<LockerSize>(v));
            entity.Property(b => b.Status).HasConversion(v => v.ToString(), v => Enum.Parse<BookingStatus>(v));
        });

        // === Ánh xạ bảng Payments ===
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.ToTable("Payments");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(p => p.Amount).HasColumnType("decimal(12,2)");
            entity.Property(p => p.Kind).HasConversion(v => v.ToString(), v => Enum.Parse<PaymentKind>(v));
            entity.Property(p => p.Status).HasConversion(v => v.ToString(), v => Enum.Parse<PaymentStatus>(v));
        });

        // === Ánh xạ bảng IoTDevices ===
        modelBuilder.Entity<IoTDevice>(entity =>
        {
            entity.ToTable("IoTDevices");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(d => d.DeviceCode).IsRequired().HasMaxLength(50);

            entity.HasOne(d => d.Station)
                .WithMany()
                .HasForeignKey(d => d.StationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // === Ánh xạ bảng SecurityIncidents ===
        modelBuilder.Entity<SecurityIncident>(entity =>
        {
            entity.ToTable("SecurityIncidents");
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(i => i.IncidentCode).IsRequired().HasMaxLength(50);
            entity.Property(i => i.Type).HasConversion(v => v.ToString(), v => Enum.Parse<IncidentType>(v));
            entity.Property(i => i.Status).HasConversion(v => v.ToString(), v => Enum.Parse<IncidentStatus>(v));

            entity.HasOne(i => i.ReportedByUser)
                .WithMany()
                .HasForeignKey(i => i.ReportedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(i => i.ResolvedByUser)
                .WithMany()
                .HasForeignKey(i => i.ResolvedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // === Ánh xạ bảng MaintenanceTickets ===
        modelBuilder.Entity<MaintenanceTicket>(entity =>
        {
            entity.ToTable("MaintenanceTickets");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(m => m.TicketCode).IsRequired().HasMaxLength(50);
            entity.Property(m => m.Status).HasConversion(v => v.ToString(), v => Enum.Parse<MaintenanceStatus>(v));

            entity.HasOne(m => m.ReportedByUser)
                .WithMany()
                .HasForeignKey(m => m.ReportedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(m => m.AssignedStaff)
                .WithMany()
                .HasForeignKey(m => m.AssignedStaffId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // === Ánh xạ bảng AbandonedPropertyRecords ===
        modelBuilder.Entity<AbandonedPropertyRecord>(entity =>
        {
            entity.ToTable("AbandonedPropertyRecords");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(a => a.RecordCode).IsRequired().HasMaxLength(50);
            entity.Property(a => a.Status).HasConversion(v => v.ToString(), v => Enum.Parse<AbandonedPropertyStatus>(v));

            entity.HasOne(a => a.StaffWitness)
                .WithMany()
                .HasForeignKey(a => a.StaffWitnessId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(a => a.AdminApproval)
                .WithMany()
                .HasForeignKey(a => a.AdminApprovalId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // === Ánh xạ bảng AuditLogs ===
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Id).HasDefaultValueSql("newsequentialid()");
            entity.Property(l => l.Action).IsRequired().HasMaxLength(100);
            entity.Property(l => l.TargetEntity).IsRequired().HasMaxLength(100);
        });
    }
}
