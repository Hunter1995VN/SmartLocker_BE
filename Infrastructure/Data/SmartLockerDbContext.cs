namespace Infrastructure.Data;

using Application.Interfaces;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

public class SmartLockerDbContext : DbContext, ISmartLockerDbContext
{
    public SmartLockerDbContext(DbContextOptions<SmartLockerDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
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
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

        // Configure database triggers so EF Core does NOT use the OUTPUT clause (which causes SQL Server error 334)
        modelBuilder.Entity<Locker>().ToTable(tb => tb.HasTrigger("trg_Lockers_UpdatedAt"));
        modelBuilder.Entity<Station>().ToTable(tb => tb.HasTrigger("trg_Stations_UpdatedAt"));
        modelBuilder.Entity<User>().ToTable(tb => tb.HasTrigger("trg_Users_UpdatedAt"));
        modelBuilder.Entity<Booking>().ToTable(tb => tb.HasTrigger("trg_Bookings_UpdatedAt"));
        modelBuilder.Entity<Payment>().ToTable(tb => tb.HasTrigger("trg_Payments_UpdatedAt"));
        modelBuilder.Entity<SecurityIncident>().ToTable(tb => tb.HasTrigger("trg_SecurityIncidents_UpdatedAt"));
        modelBuilder.Entity<MaintenanceTicket>().ToTable(tb => tb.HasTrigger("trg_MaintenanceTickets_UpdatedAt"));
        modelBuilder.Entity<AbandonedPropertyRecord>().ToTable(tb => tb.HasTrigger("trg_AbandonedProperty_UpdatedAt"));
        modelBuilder.Entity<IoTDevice>().ToTable(tb => tb.HasTrigger("trg_IoTDevices_UpdatedAt"));
    }
}
