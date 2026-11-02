using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.BackgroundJobs;
using Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hung.UseCases.Tests;

public class NoShowTests
{
    [Theory] [InlineData(true, "OPEN")] [InlineData(true, "CLOSED")]
    [InlineData(false, "OPEN")] [InlineData(false, "CLOSED")]
    public async Task ExpiryNeverFreesAnIssuedUnlockOrAnOpenDoor(bool pendingUnlock, string door)
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection().AddDbContext<SmartLockerDbContext>(x => x.UseInMemoryDatabase(name))
            .AddScoped<ISmartLockerDbContext>(x => x.GetRequiredService<SmartLockerDbContext>()).BuildServiceProvider();
        using (services)
        {
            var id = Guid.NewGuid();
            using (var seed = services.CreateScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<SmartLockerDbContext>();
                var b = new Booking { Id = id, Status = BookingStatus.CONFIRMED, CreatedAt = DateTime.UtcNow.AddHours(-2),
                    CheckedInAt = pendingUnlock ? DateTime.UtcNow.AddMinutes(-40) : null,
                    Locker = new Locker { Id = Guid.NewGuid(), DoorState = door, HealthStatus = LockerHealthStatus.HEALTHY,
                        BusinessStatus = LockerBusinessStatus.RESERVED },
                    AccessCredential = new AccessCredential { Id = Guid.NewGuid(), BookingId = id, IsActive = true } };
                b.Payments.Add(new Payment { Id = Guid.NewGuid(), BookingId = id, Kind = PaymentKind.BASE,
                    Status = PaymentStatus.PAID, PaidAt = DateTime.UtcNow.AddHours(-2) });
                db.Add(b); await db.SaveChangesAsync();
            }
            await new NoShowJob(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<NoShowJob>.Instance).RunOnceAsync();
            using var scope = services.CreateScope();
            var current = await scope.ServiceProvider.GetRequiredService<SmartLockerDbContext>().Bookings
                .Include(x => x.Locker).Include(x => x.AccessCredential).SingleAsync();
            Assert.Equal(pendingUnlock ? BookingStatus.CONFIRMED : BookingStatus.NO_SHOW, current.Status);
            Assert.Equal(!pendingUnlock && door == "CLOSED" ? LockerBusinessStatus.AVAILABLE : LockerBusinessStatus.RESERVED,
                current.Locker!.BusinessStatus);
            Assert.Equal(pendingUnlock, current.AccessCredential!.IsActive);
        }
    }
}
