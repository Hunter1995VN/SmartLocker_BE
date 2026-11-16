using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hung.UseCases.Tests;

/// <summary>
/// GET /api/admin/manual-refunds/candidates returned 500 ("Include path Booking->Payments results in a cycle")
/// because the query started from Payments, included Booking.Payments and used AsNoTracking.
/// </summary>
public class ManualRefundCandidatesTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task CandidatesListLoadsPaidPaymentsWhoseBookingHasSeveralPayments()
    {
        var user = new User { Id = Guid.NewGuid(), FullName = "Khách A", Email = "a@e2e.local", Phone = "0900000001" };
        var station = new Station { Id = Guid.NewGuid(), Name = "Trạm A" };
        var booking = new Booking { Id = Guid.NewGuid(), User = user, Station = station, BookingCode = "BK-REFUND-1" };
        db.Add(new Payment { Id = Guid.NewGuid(), Booking = booking, Kind = PaymentKind.BASE, Status = PaymentStatus.PAID, Amount = 75000, OrderCode = "OC-1" });
        db.Add(new Payment { Id = Guid.NewGuid(), Booking = booking, Kind = PaymentKind.EXTENSION, Status = PaymentStatus.PAID, Amount = 25000, OrderCode = "OC-2" });
        await db.SaveChangesAsync();

        var service = new ManualRefundService(db, null!, null!, NullLogger<ManualRefundService>.Instance);

        var result = await service.GetRefundableCandidatesAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Count);
        Assert.All(result.Data, c => Assert.Equal("BK-REFUND-1", c.BookingCode));
    }

    public void Dispose() => db.Dispose();
}
