using Application.DTOs.Kiosk;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Mqtt;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using Xunit;

namespace Hung.UseCases.Tests;

public class KioskPickupTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IMqttPublishService> mqtt = new();
    private readonly Mock<ILockerNotificationService> notifications = new();
    private readonly Mock<IPricingService> pricing = new();
    private readonly Booking booking;
    private readonly Locker locker;
    private KioskService Service => new(db, mqtt.Object, notifications.Object, pricing.Object, NullLogger<KioskService>.Instance);

    public KioskPickupTests()
    {
        var station = new Station { Id = Guid.NewGuid(), Status = StationStatus.ACTIVE };
        var device = new IoTDevice { Id = Guid.NewGuid(), StationId = station.Id, DeviceCode = "TEST-DEVICE", LastSeenAt = DateTime.UtcNow };
        locker = new Locker { Id = Guid.NewGuid(), StationId = station.Id, IoTDeviceId = device.Id,
            LockerCode = "S-01", Size = LockerSize.S, DoorState = "CLOSED", BusinessStatus = LockerBusinessStatus.RESERVED };
        booking = new Booking { Id = Guid.NewGuid(), BookingCode = "BK-TEST-CHECKIN", StationId = station.Id,
            Station = station, LockerId = locker.Id, Locker = locker, Size = LockerSize.S,
            Status = BookingStatus.CONFIRMED, StartAt = DateTime.UtcNow, EndAt = DateTime.UtcNow.AddHours(3) };
        booking.AccessCredential = new AccessCredential { Id = Guid.NewGuid(), BookingId = booking.Id,
            IsActive = true, AccessCodeHash = OnlineTicket.HashPin("01234567") };
        OnlineTicket.Rotate(booking.AccessCredential, station.Id, DateTime.UtcNow);
        booking.Payments.Add(new Payment { Id = Guid.NewGuid(), BookingId = booking.Id, Kind = PaymentKind.BASE,
            Status = PaymentStatus.PAID, PaidAt = DateTime.UtcNow });
        db.AddRange(station, device, locker, booking); db.SaveChanges();
        pricing.Setup(p => p.GetActivePolicyAsync(station.Id, LockerSize.S)).ReturnsAsync(new PricingPolicy());
        mqtt.Setup(m => m.PublishUnlockCommandAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>())).ReturnsAsync(true);
    }

    private KioskCheckInRequest Request(string type = "ACCESS_CODE", string payload = "01234567") =>
        new() { StationId = booking.StationId, CredentialType = type, Payload = payload };

    private void PreparePickup()
    {
        booking.Status = BookingStatus.STORED;
        locker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
    }
    private Task<Application.DTOs.Common.ApiResponse<KioskCheckOutResponse>> Pickup(string pin = "01234567") =>
        Service.CheckOutAsync(new() { StationId = booking.StationId, CredentialType = "ACCESS_CODE", Payload = pin });

    [Fact] public async Task PickupCompletesOnlyAfterOpenCloseAndRevokesTicket()
    {
        PreparePickup(); await db.SaveChangesAsync();
        Assert.True((await Pickup()).Success);
        Assert.Equal(BookingStatus.STORED, booking.Status);
        var handler = new DoorEventHandler(db, notifications.Object, NullLogger<DoorEventHandler>.Instance);
        var time = DateTimeOffset.UtcNow;
        async Task Event(string state, int ms) => await handler.HandleAsync("smartlocker/devices/TEST-DEVICE/door",
            JsonSerializer.Serialize(new DoorEvent(1, Guid.NewGuid(), "TEST-DEVICE", locker.Id, state, time.AddMilliseconds(ms)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), false, default);
        await Event("CLOSED", 1); Assert.Equal(BookingStatus.STORED, booking.Status);
        await Event("OPEN", 2); await Event("CLOSED", 3);
        Assert.Equal(BookingStatus.COMPLETED, booking.Status);
        Assert.Equal(LockerBusinessStatus.AVAILABLE, locker.BusinessStatus);
        Assert.False(booking.AccessCredential!.IsActive);
        Assert.NotNull(booking.AccessCredential.RevokedAt);
    }

    [Fact] public async Task OverdueCalculationDoesNotDependOnStaleFlag()
    {
        booking.Status = BookingStatus.STORED; locker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
        booking.IsOverdue = false; booking.EndAt = DateTime.UtcNow.AddHours(-2); await db.SaveChangesAsync();
        pricing.Setup(p => p.CalculateOverdueFee(booking, It.IsAny<PricingPolicy>())).Returns(30000);
        var result = await Service.CheckOutAsync(new() { StationId = booking.StationId, CredentialType = "ACCESS_CODE", Payload = "01234567" });
        Assert.True(result.Data!.IsOverdue); Assert.Equal(30000, result.Data.OverdueFee); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task FeeSessionIsNotAnUnlockSessionAndCannotBeChangedToAnotherCredentialMode()
    {
        PreparePickup(); await db.SaveChangesAsync();
        pricing.Setup(x => x.CalculateOverdueFee(booking, It.IsAny<PricingPolicy>())).Returns(30000);
        var result = await Pickup();
        Assert.True(result.Data!.IsOverdue); Assert.Equal(64, result.Data.SessionToken!.Length);
        Assert.False((await Service.GetOperationStatusAsync(new() { StationId = booking.StationId,
            BookingId = booking.Id, SessionToken = result.Data.SessionToken })).Success);
        Assert.False((await Service.CheckOutAsync(new() { StationId = booking.StationId, CredentialType = "QR",
            Payload = "01234567", SessionToken = result.Data.SessionToken })).Success);
        Assert.Null(booking.CheckedOutAt); mqtt.VerifyNoOtherCalls();
    }

    [Fact] public async Task EstablishedQrFeeSessionSurvivesQrExpiryButNotCredentialRevocation()
    {
        PreparePickup(); await db.SaveChangesAsync();
        var qr = booking.AccessCredential!.QrPayload;
        pricing.Setup(x => x.CalculateOverdueFee(booking, It.IsAny<PricingPolicy>())).Returns(30000);
        var first = await Service.CheckOutAsync(new() { StationId = booking.StationId, CredentialType = "QR", Payload = qr });
        booking.AccessCredential.QrExpiresAt = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        var continued = await Service.CheckOutAsync(new() { StationId = booking.StationId, CredentialType = "QR", Payload = qr, SessionToken = first.Data!.SessionToken });
        Assert.True(continued.Success); Assert.True(continued.Data!.IsOverdue);
        booking.AccessCredential.IsActive = false; await db.SaveChangesAsync();
        Assert.False((await Service.CheckOutAsync(new() { StationId = booking.StationId, CredentialType = "QR", Payload = qr, SessionToken = continued.Data.SessionToken })).Success);
        mqtt.VerifyNoOtherCalls();
    }

    [Fact] public async Task FailedPublishCannotCompletePickup()
    {
        PreparePickup(); await db.SaveChangesAsync();
        mqtt.Setup(x => x.PublishUnlockCommandAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>())).ReturnsAsync(false);
        Assert.False((await Pickup()).Success); Assert.Null(booking.CheckedOutAt);
        Assert.Equal(BookingStatus.STORED, booking.Status); Assert.True(booking.AccessCredential!.IsActive);
    }
    [Theory] [InlineData("OFFLINE")] [InlineData("STALE")]
    public async Task UnavailableDeviceCannotReceivePickup(string state)
    {
        PreparePickup(); var device = await db.IoTDevices.SingleAsync();
        if (state == "OFFLINE") device.ConnectivityStatus = "OFFLINE";
        else device.LastSeenAt = DateTime.UtcNow.AddMinutes(-2);
        await db.SaveChangesAsync(); Assert.False((await Pickup()).Success); mqtt.VerifyNoOtherCalls();
    }
    [Theory] [InlineData("INACTIVE")] [InlineData("REVOKED")] [InlineData("LOCKED")]
    public async Task IneligibleCredentialNeverUnlocks(string state)
    {
        PreparePickup(); var c = booking.AccessCredential!;
        if (state == "INACTIVE") c.IsActive = false;
        if (state == "REVOKED") c.RevokedAt = DateTime.UtcNow;
        if (state == "LOCKED") c.AuthLockUntil = DateTime.UtcNow.AddMinutes(10);
        await db.SaveChangesAsync(); Assert.False((await Pickup()).Success); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task AlreadyPaidOverdueAmountIsNotChargedTwice()
    {
        PreparePickup();
        db.Payments.Add(new Payment { Id = Guid.NewGuid(), BookingId = booking.Id,
            Kind = PaymentKind.OVERDUE, Status = PaymentStatus.PAID, Amount = 30000 });
        await db.SaveChangesAsync();
        pricing.Setup(x => x.CalculateOverdueFee(booking, It.IsAny<PricingPolicy>())).Returns(30000);
        var result = await Pickup(); Assert.True(result.Success); Assert.False(result.Data!.IsOverdue);
    }
    [Fact] public async Task DuplicatePickupRequestDoesNotSendAnotherCommand()
    {
        PreparePickup(); await db.SaveChangesAsync();
        Assert.True((await Pickup()).Success); Assert.False((await Pickup()).Success);
        mqtt.Verify(x => x.PublishUnlockCommandAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Once);
    }
    [Fact] public async Task CustomerConfirmationCannotBypassDoorEvents()
    {
        PreparePickup(); booking.CheckedOutAt = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
        Assert.False((await Service.ManualConfirmAsync(new() { BookingId = booking.Id, StationId = booking.StationId, LockerId = locker.Id })).Success);
        Assert.Equal(BookingStatus.STORED, booking.Status); Assert.True(booking.AccessCredential!.IsActive);
    }
    [Fact] public async Task FeePaymentRequiresTheExactCredentialBoundSessionAndOnlineDevice()
    {
        PreparePickup(); booking.UserId = Guid.NewGuid(); await db.SaveChangesAsync();
        pricing.Setup(x => x.CalculateOverdueFee(booking, It.IsAny<PricingPolicy>())).Returns(30000);
        var fee = (await Pickup()).Data!;
        var payments = new Mock<IBookingService>();
        payments.Setup(x => x.PayOverdueFeeAsync(booking.UserId, booking.Id)).ReturnsAsync(
            Application.DTOs.Common.ApiResponse<Application.DTOs.Booking.CreateBookingResponse>.SuccessResponse(new() { Amount = 30000 }));
        var service = new KioskService(db, mqtt.Object, notifications.Object, pricing.Object, NullLogger<KioskService>.Instance, payments.Object);
        var request = new KioskFeePaymentRequest { BookingId = booking.Id, StationId = booking.StationId,
            CredentialType = "ACCESS_CODE", Payload = "01234567", SessionToken = fee.SessionToken };
        Assert.True((await service.PayOverdueAsync(request)).Success);
        request.Payload = "76543210"; Assert.False((await service.PayOverdueAsync(request)).Success);
        request.Payload = "01234567";
        (await db.IoTDevices.SingleAsync()).ConnectivityStatus = "OFFLINE"; await db.SaveChangesAsync();
        Assert.False((await service.PayOverdueAsync(request)).Success);
        payments.Verify(x => x.PayOverdueFeeAsync(booking.UserId, booking.Id), Times.Once); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task OverdueLinkUsesOutstandingAmountAndDoesNotDependOnStaleFlag()
    {
        PreparePickup(); booking.UserId = Guid.NewGuid(); booking.IsOverdue = false;
        db.Payments.Add(new Payment { Id = Guid.NewGuid(), BookingId = booking.Id, Kind = PaymentKind.OVERDUE,
            Status = PaymentStatus.PAID, Amount = 10000 });
        await db.SaveChangesAsync();
        pricing.Setup(x => x.CalculateOverdueFee(booking, It.IsAny<PricingPolicy>())).Returns(30000);
        var gateway = new Mock<IPaymentGatewayService>();
        gateway.Setup(x => x.CreatePaymentLinkAsync(It.IsAny<long>(), 20000, It.IsAny<string>(), "", ""))
            .ReturnsAsync(new Application.DTOs.Payment.PaymentLinkResult { CheckoutUrl = "https://unit.invalid/payment", QrCode = "UNIT-ONLY" });
        var service = new BookingService(db, pricing.Object, gateway.Object, Mock.Of<IPaymentService>(), Mock.Of<IOfflineTicketService>(), NullLogger<BookingService>.Instance);
        var result = await service.PayOverdueFeeAsync(booking.UserId, booking.Id);
        Assert.True(result.Success); Assert.Equal(20000, result.Data!.Amount); Assert.Equal("UNIT-ONLY", result.Data.QrCode);
        Assert.Equal(20000, (await db.Payments.SingleAsync(x => x.Kind == PaymentKind.OVERDUE && x.Status == PaymentStatus.PENDING)).Amount);
    }
    public void Dispose() => db.Dispose();
}
