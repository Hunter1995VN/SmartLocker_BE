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
                    .Include(b => b.Payments)
                    .Where(b => b.Status == BookingStatus.CONFIRMED)
                    .ToListAsync(stoppingToken);

                var now = DateTime.UtcNow;
                int count = 0;

                foreach (var booking in confirmedBookings)
                {
                    var basePayment = booking.Payments.FirstOrDefault(p => p.Kind == PaymentKind.BASE && p.Status == PaymentStatus.PAID);
                    var confirmedAt = basePayment?.PaidAt ?? booking.CreatedAt;
                    // Thời gian giữ chỗ 30 phút từ lúc thanh toán thành công
                    var holdMinutes = booking.PricingPolicy?.NoShowWindowMinutes > 0 ? booking.PricingPolicy.NoShowWindowMinutes : 30;
                    var limitTime = confirmedAt.AddMinutes(holdMinutes);

                    if (now > limitTime)
                    {
                        booking.Status = BookingStatus.NO_SHOW;
                        booking.UpdatedAt = now;
                        count++;
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
