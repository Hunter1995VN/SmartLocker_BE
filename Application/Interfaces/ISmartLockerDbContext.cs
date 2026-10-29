namespace Application.Interfaces;

using Domain.Entities;
using Microsoft.EntityFrameworkCore;

public interface ISmartLockerDbContext
{
    DbSet<LostItemReport> LostItemReports { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<StationReview> StationReviews { get; }
    DbSet<User> Users { get; }
    DbSet<Station> Stations { get; }
    DbSet<Locker> Lockers { get; }
    DbSet<PricingPolicy> PricingPolicies { get; }
    DbSet<Booking> Bookings { get; }
    DbSet<Payment> Payments { get; }
    DbSet<BookingExtension> BookingExtensions { get; }
    DbSet<AccessCredential> AccessCredentials { get; }
    DbSet<IoTDevice> IoTDevices { get; }
    DbSet<SecurityIncident> SecurityIncidents { get; }
    DbSet<MaintenanceTicket> MaintenanceTickets { get; }
    DbSet<AbandonedPropertyRecord> AbandonedPropertyRecords { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Voucher> Vouchers { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database { get; }
}
