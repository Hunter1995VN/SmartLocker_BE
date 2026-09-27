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

public class NoShowJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NoShowJob> _logger;

    public NoShowJob(IServiceScopeFactory scopeFactory, ILogger<NoShowJob> logger)
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
                
                var confirmedBookings = await context.Bookings
                    .Include(b => b.PricingPolicy)
                    .Where(b => b.Status == BookingStatus.CONFIRMED)
                    .ToListAsync(stoppingToken);

                var now = DateTime.UtcNow;
                int count = 0;

                foreach (var booking in confirmedBookings)
                {
                    var policy = booking.PricingPolicy;
                    if (policy == null && booking.PricingPolicyId != null)
                    {
                        policy = await context.PricingPolicies.FindAsync(new object[] { booking.PricingPolicyId }, stoppingToken);
                    }

                    if (policy != null)
                    {
                        var limitTime = booking.StartAt.AddMinutes(policy.NoShowWindowMinutes);
                        if (now > limitTime)
                        {
                            booking.Status = BookingStatus.NO_SHOW;
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    await context.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"Marked {count} bookings as NO_SHOW.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in NoShowJob");
            }

            await Task.Delay(TimeSpan.FromSeconds(300), stoppingToken);
        }
    }
}
