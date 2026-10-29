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

public class KioskTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IMqttPublishService> mqtt = new();
    private readonly Mock<ILockerNotificationService> notifications = new();
    private readonly Mock<IPricingService> pricing = new();
    private readonly Booking booking;
    private readonly Locker locker;
    private KioskService Service => new(db, mqtt.Object, notifications.Object, pricing.Object, NullLogger<KioskService>.Instance);

    public KioskTests()
    {
        var station = new Station { Id = Guid.NewGuid(), Status = StationStatus.ACTIVE };
        var device = new IoTDevice { Id = Guid.NewGuid(), StationId = station.Id, DeviceCode = "TEST-DEVICE" };
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

    [Fact] public async Task NumericPinIsHashedAndCommandDoesNotCompleteCheckIn()
    {
        var result = await Service.CheckInAsync(Request());
        Assert.True(result.Success); Assert.Equal(BookingStatus.CONFIRMED, booking.Status);
        Assert.Equal("CLOSED", result.Data!.DoorState);
        mqtt.Verify(m => m.PublishUnlockCommandAsync("TEST-DEVICE", locker.Id, "S-01", null, 500), Times.Once);
    }
    [Theory]
    [InlineData("ACCESS_CODE", "BK-TEST-CHECKIN")]
    [InlineData("ACCESS_CODE", "123456")]
    [InlineData("QR", "BK-TEST-CHECKIN")]
    [InlineData("UNKNOWN", "01234567")]
    [InlineData("QR", "{\"bookingCode\":\"BK-TEST-CHECKIN\"}")]
    public async Task IdentifiersAndInvalidCredentialsNeverUnlock(string type, string payload)
    {
        Assert.False((await Service.CheckInAsync(Request(type, payload))).Success); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task WrongStationNeverUnlocksEvenForTestBooking()
    {
        var request = Request(); request.StationId = Guid.NewGuid();
        Assert.False((await Service.CheckInAsync(request)).Success); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task UnpaidBookingIsNotAutoConfirmed()
    {
        booking.Status = BookingStatus.PENDING_PAYMENT; await db.SaveChangesAsync();
        Assert.False((await Service.CheckInAsync(Request("QR", booking.AccessCredential!.QrPayload))).Success);
        Assert.Equal(BookingStatus.PENDING_PAYMENT, booking.Status); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task ExpiredQrIsRejected()
    {
        booking.AccessCredential!.QrExpiresAt = DateTime.UtcNow.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.False((await Service.CheckInAsync(Request("QR", booking.AccessCredential.QrPayload))).Success);
        mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task FailedMqttDoesNotReportSuccess()
    {
        mqtt.Setup(m => m.PublishUnlockCommandAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>())).ReturnsAsync(false);
        Assert.False((await Service.CheckInAsync(Request())).Success); Assert.Null(booking.CheckedInAt);
        Assert.Equal(BookingStatus.CONFIRMED, booking.Status);
    }
    [Fact] public async Task RepeatedRequestDoesNotPublishTwice()
    {
        Assert.True((await Service.CheckInAsync(Request())).Success);
        Assert.False((await Service.CheckInAsync(Request())).Success);
        mqtt.Verify(m => m.PublishUnlockCommandAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Once);
    }
    [Fact] public async Task NoFallbackToAnotherStationLocker()
    {
        booking.Locker = null; booking.LockerId = null; locker.StationId = Guid.NewGuid(); await db.SaveChangesAsync();
        Assert.False((await Service.CheckInAsync(Request())).Success); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task OverdueCalculationDoesNotDependOnStaleFlag()
    {
        booking.Status = BookingStatus.STORED; locker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
        booking.IsOverdue = false; booking.EndAt = DateTime.UtcNow.AddHours(-2); await db.SaveChangesAsync();
        pricing.Setup(p => p.CalculateOverdueFee(booking, It.IsAny<PricingPolicy>())).Returns(30000);
        var result = await Service.CheckOutAsync(new() { StationId = booking.StationId, CredentialType = "ACCESS_CODE", Payload = "01234567" });
        Assert.True(result.Data!.IsOverdue); Assert.Equal(30000, result.Data.OverdueFee); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task DoorMustOpenAfterCommandBeforeCloseCompletesBooking()
    {
        var handler = new DoorEventHandler(db, notifications.Object, NullLogger<DoorEventHandler>.Instance);
        booking.CheckedInAt = DateTime.UtcNow.AddSeconds(-5); await db.SaveChangesAsync();
        async Task Event(string state, int seconds) => await handler.HandleAsync("smartlocker/devices/TEST-DEVICE/door",
            JsonSerializer.Serialize(new DoorEvent(1, Guid.NewGuid(), "TEST-DEVICE", locker.Id, state, DateTimeOffset.UtcNow.AddSeconds(seconds)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), false, default);
        await Event("CLOSED", -3); Assert.Equal(BookingStatus.CONFIRMED, booking.Status);
        await Event("OPEN", -2); await Event("CLOSED", -1);
        Assert.Equal(BookingStatus.STORED, booking.Status); Assert.Equal(LockerBusinessStatus.OCCUPIED, locker.BusinessStatus);
    }
    [Fact] public void RotationChangesTokenAndPreservesPinHash()
    {
        var c = booking.AccessCredential!; var original = c.QrPayload; var hash = c.AccessCodeHash;
        var now = DateTime.UtcNow; OnlineTicket.Rotate(c, booking.StationId, now);
        Assert.NotEqual(original, c.QrPayload); Assert.Equal(now.AddSeconds(60), c.QrExpiresAt); Assert.Equal(hash, c.AccessCodeHash);
    }
    public void Dispose() => db.Dispose();
}
