namespace Infrastructure.BackgroundJobs;

using Application.Interfaces;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class BookingExpirationJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BookingExpirationJob> _logger;

    public BookingExpirationJob(IServiceScopeFactory scopeFactory, ILogger<BookingExpirationJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ISmartLockerDbContext>();

                var now = DateTime.UtcNow;
                var expiredBookings = await context.Bookings
                    .Include(b => b.Payments)
                    .Where(b => b.Status == BookingStatus.PENDING_PAYMENT && b.PaymentExpiresAt < now)
                    .ToListAsync(stoppingToken);

                if (expiredBookings.Any())
                {
                    foreach (var booking in expiredBookings)
                    {
                        booking.Status = BookingStatus.EXPIRED;
                        var pendingPayments = booking.Payments.Where(p => p.Status == PaymentStatus.PENDING).ToList();
                        foreach (var payment in pendingPayments)
                        {
                            payment.Status = PaymentStatus.EXPIRED;
                        }
                    }

                    await context.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"Expired {expiredBookings.Count} bookings.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BookingExpirationJob");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }
}
