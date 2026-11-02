namespace Infrastructure.BackgroundJobs;

using Application.Interfaces;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class NoShowJob(IServiceScopeFactory scopeFactory, ILogger<NoShowJob> logger) : BackgroundService
{
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scanScope = scopeFactory.CreateScope();
        var scan = scanScope.ServiceProvider.GetRequiredService<ISmartLockerDbContext>();
        var ids = await scan.Bookings.AsNoTracking()
            .Where(b => b.Status == BookingStatus.CONFIRMED && b.CheckedInAt == null)
            .Select(b => b.Id).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            using var scope = scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ISmartLockerDbContext>();
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                if (context.Database.IsRelational() && context is DbContext tracked) tracked.ChangeTracker.Clear();
                await using var transaction = context.Database.IsRelational()
                    ? await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken) : null;
                // Check under the same isolation used by check-in. Issued unlocks awaiting
                // physical door events must never be treated as no-shows.
                var booking = await context.Bookings.Include(b => b.PricingPolicy).Include(b => b.Payments)
                    .Include(b => b.Locker).Include(b => b.AccessCredential)
                    .FirstOrDefaultAsync(b => b.Id == id && b.Status == BookingStatus.CONFIRMED
                        && b.CheckedInAt == null, cancellationToken);
                if (booking == null) return;
                var confirmedAt = booking.Payments.FirstOrDefault(p => p.Kind == PaymentKind.BASE
                    && p.Status == PaymentStatus.PAID)?.PaidAt ?? booking.CreatedAt;
                var minutes = booking.PricingPolicy?.NoShowWindowMinutes > 0
                    ? booking.PricingPolicy.NoShowWindowMinutes : 30;
                var now = DateTime.UtcNow;
                if (now <= confirmedAt.AddMinutes(minutes)) return;
                booking.Status = BookingStatus.NO_SHOW;
                booking.UpdatedAt = now;
                if (booking.AccessCredential is { } credential)
                {
                    credential.IsActive = false;
                    credential.RevokedAt = now;
                    credential.RevokeReason = "NO_SHOW";
                }
                if (booking.Locker is { BusinessStatus: LockerBusinessStatus.RESERVED } locker
                    && locker.HealthStatus == LockerHealthStatus.HEALTHY
                    && !string.Equals(locker.DoorState, "OPEN", StringComparison.OrdinalIgnoreCase)
                    && !await context.Bookings.AnyAsync(b => b.Id != id && b.LockerId == locker.Id
                        && (b.Status == BookingStatus.PENDING_PAYMENT || b.Status == BookingStatus.CONFIRMED
                            || b.Status == BookingStatus.STORED), cancellationToken))
                {
                    locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
                    locker.UpdatedAt = now;
                }
                await context.SaveChangesAsync(cancellationToken);
                if (transaction != null) await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Marked booking {BookingId} as NO_SHOW", id);
            });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Error in NoShowJob"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
