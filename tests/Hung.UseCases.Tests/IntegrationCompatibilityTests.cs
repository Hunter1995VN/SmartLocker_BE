using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Hung.UseCases.Tests;

// Isolated regression for the DTO/model merge fixes; no shared SQL or gateway writes.
public sealed class IntegrationCompatibilityTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid owner = Guid.NewGuid();
    private BookingService Service => new(db, Mock.Of<IPricingService>(), Mock.Of<IPaymentGatewayService>(),
        Mock.Of<IPaymentService>(), Mock.Of<IOfflineTicketService>(), NullLogger<BookingService>.Instance);

    private Booking Seed(bool paid)
    {
        var station = new Station { Id = Guid.NewGuid(), Name = "UNIT-STATION" };
        var policy = new PricingPolicy { Id = Guid.NewGuid(), StationId = station.Id, RefundCutoffHours = 2,
            RefundRateEarly = 1m, RefundRateLate = .5m };
        var booking = new Booking { Id = Guid.NewGuid(), BookingCode = "UNIT-CANCEL", UserId = owner,
            StationId = station.Id, Station = station, PricingPolicy = policy, PricingPolicyId = policy.Id,
            Status = paid ? BookingStatus.CONFIRMED : BookingStatus.PENDING_PAYMENT,
            StartAt = DateTime.UtcNow.AddHours(4), EndAt = DateTime.UtcNow.AddHours(5), BaseAmount = 99000 };
        if (paid) booking.Payments.Add(new() { Id = Guid.NewGuid(), BookingId = booking.Id,
            Kind = PaymentKind.BASE, Status = PaymentStatus.PAID, Amount = 40000 });
        db.Add(booking); db.SaveChanges(); return booking;
    }

    [Theory] [InlineData(false, 0)] [InlineData(true, 40000)]
    public async Task CancellationPreviewUsesOnlyVerifiedPaymentsAndPopulatesClientContract(bool paid, int expected)
    {
        var booking = Seed(paid);
        var result = await Service.GetCancelPreviewAsync(owner, booking.Id);
        Assert.True(result.Success); var preview = result.Data!;
        Assert.Equal((decimal)expected, preview.PaidAmount); Assert.Equal((decimal)expected, preview.RefundAmount);
        Assert.True(preview.CanCancel); Assert.Equal(booking.Status.ToString(), preview.Status);
        Assert.Equal(booking.StartAt, preview.StartAt); Assert.Equal("UNIT-STATION", preview.StationName);
        Assert.Equal(0, preview.DailyCancellationCount);
    }

    [Fact] public async Task PreviewReturnsTheRealDailyCancellationCount()
    {
        var booking = Seed(true);
        for (var i = 0; i < 2; i++) db.Bookings.Add(new() { Id = Guid.NewGuid(), UserId = owner,
            StationId = booking.StationId, Status = BookingStatus.CANCELLED, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var preview = (await Service.GetCancelPreviewAsync(owner, booking.Id)).Data!;
        Assert.Equal(2, preview.DailyCancellationCount); Assert.True(preview.IsDailyLimitExceeded);
        Assert.Equal(0m, preview.RefundAmount);
    }
    [Fact] public async Task SimulationRouteCannotBackdateAnActualBooking()
    {
        var booking = Seed(true); booking.Status = BookingStatus.STORED; await db.SaveChangesAsync();
        var start = booking.StartAt; var end = booking.EndAt;
        var result = await Service.SimulateOverdueAsync(owner, booking.Id, 2);
        Assert.False(result.Success); Assert.Equal(start, booking.StartAt); Assert.Equal(end, booking.EndAt);
        Assert.False(booking.IsOverdue); Assert.Equal(BookingStatus.STORED, booking.Status);
    }
    [Fact] public void VoucherDateAliasesCannotInventExtraSqlColumns()
    {
        var entity = db.Model.FindEntityType(typeof(Voucher))!;
        Assert.NotNull(entity.FindProperty(nameof(Voucher.StartDate)));
        Assert.NotNull(entity.FindProperty(nameof(Voucher.EndDate)));
        Assert.Null(entity.FindProperty(nameof(Voucher.ValidFrom)));
        Assert.Null(entity.FindProperty(nameof(Voucher.ValidTo)));
    }
    public void Dispose() => db.Dispose();
}
