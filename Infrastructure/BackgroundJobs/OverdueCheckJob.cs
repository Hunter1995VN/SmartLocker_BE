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

public class OverdueCheckJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OverdueCheckJob> _logger;

    public OverdueCheckJob(IServiceScopeFactory scopeFactory, ILogger<OverdueCheckJob> logger)
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

                var storedBookings = await context.Bookings
                    .Include(b => b.PricingPolicy)
                    .Where(b => b.Status == BookingStatus.STORED && b.IsOverdue == false)
                    .ToListAsync(stoppingToken);

                var now = DateTime.UtcNow;
                int count = 0;

                foreach (var booking in storedBookings)
                {
                    var policy = booking.PricingPolicy;
                    if (policy == null && booking.PricingPolicyId != null)
                    {
                        policy = await context.PricingPolicies.FindAsync(new object[] { booking.PricingPolicyId }, stoppingToken);
                    }

                    if (policy == null)
                    {
                        policy = await context.PricingPolicies
                            .Where(p => p.StationId == booking.StationId && p.Size == booking.Size && p.EffectiveTo == null)
                            .OrderByDescending(p => p.EffectiveFrom)
                            .FirstOrDefaultAsync(stoppingToken);
                    }

                    var graceMinutes = policy?.GracePeriodMinutes ?? 15;
                    var overdueLimit = booking.EndAt.AddMinutes(graceMinutes);
                    if (now > overdueLimit)
                    {
                        booking.IsOverdue = true;
                        booking.OverdueSince = overdueLimit;
                        count++;
                    }
                }

                if (count > 0)
                {
                    await context.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation($"Marked {count} bookings as Overdue.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OverdueCheckJob");
            }

            // Kiểm tra mỗi 60 giây để đảm bảo cập nhật trạng thái quá hạn nhanh chóng và chính xác nhất
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }
}
