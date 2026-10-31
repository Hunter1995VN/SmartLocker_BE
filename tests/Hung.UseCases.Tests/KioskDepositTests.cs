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

public class KioskDepositTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IMqttPublishService> mqtt = new();
    private readonly Mock<ILockerNotificationService> notifications = new();
    private readonly Mock<IPricingService> pricing = new();
    private readonly Booking booking;
    private readonly Locker locker;
    private KioskService Service => new(db, mqtt.Object, notifications.Object, pricing.Object, NullLogger<KioskService>.Instance);

    public KioskDepositTests()
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

    [Fact] public async Task NumericPinIsHashedAndCommandDoesNotCompleteCheckIn()
    {
        var result = await Service.CheckInAsync(Request());
        Assert.True(result.Success); Assert.Equal(BookingStatus.CONFIRMED, booking.Status);
        Assert.Equal("CLOSED", result.Data!.DoorState);
        mqtt.Verify(m => m.PublishUnlockCommandAsync("TEST-DEVICE", locker.Id, "S-01", It.Is<string>(id => id.Length == 32), 500), Times.Once);
    }
    [Fact]
    public async Task DynamicAllocationSkipsOfflineDeviceInsteadOfRejectingAnOnlineSlot()
    {
        booking.Locker = null; booking.LockerId = null;
        locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
        var offline = new IoTDevice { Id = Guid.NewGuid(), StationId = booking.StationId,
            DeviceCode = "UNIT-OFFLINE", ConnectivityStatus = "OFFLINE" };
        db.IoTDevices.Add(offline);
        db.Lockers.Add(new Locker { Id = Guid.NewGuid(), StationId = booking.StationId, Size = LockerSize.S,
            LockerCode = "A-OFFLINE", IoTDeviceId = offline.Id, BusinessStatus = LockerBusinessStatus.AVAILABLE });
        await db.SaveChangesAsync();
        var response = await Service.CheckInAsync(Request());
        Assert.True(response.Success);
        Assert.Equal(locker.Id, response.Data!.LockerId);
        mqtt.Verify(m => m.PublishUnlockCommandAsync("TEST-DEVICE", locker.Id, "S-01", It.IsAny<string?>(), 500), Times.Once);
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
    [Fact] public async Task HeartbeatUpdatesActualDeviceFieldsButNeverCompletesDoorWorkflow()
    {
        var device = await db.IoTDevices.SingleAsync();
        device.ConnectivityStatus = "OFFLINE"; device.LastSeenAt = null;
        booking.CheckedInAt = DateTime.UtcNow.AddSeconds(-5);
        await db.SaveChangesAsync();
        var handler = new HeartbeatEventHandler(db, notifications.Object);
        await handler.HandleAsync("smartlocker/devices/TEST-DEVICE/heartbeat",
            "{\"deviceCode\":\"TEST-DEVICE\",\"status\":\"ONLINE\",\"uptime\":120,\"rssi\":-55,\"doorS\":\"CLOSED\"}", false, default);
        Assert.Equal("ONLINE", device.ConnectivityStatus); Assert.NotNull(device.LastSeenAt);
        Assert.Equal<long?>(120, device.UptimeSeconds); Assert.Equal<short?>(-55, device.WifiRssi);
        Assert.Equal(BookingStatus.CONFIRMED, booking.Status);
        notifications.Verify(x => x.NotifyStationStatusChangedAsync(booking.StationId, "ONLINE", It.IsAny<DateTimeOffset>()), Times.Once);
    }

    [Theory] [InlineData(true, "TEST-DEVICE")] [InlineData(false, "OTHER-DEVICE")]
    public async Task RetainedOrMismatchedHeartbeatCannotMakeDeviceOnline(bool retained, string topicDevice)
    {
        var device = await db.IoTDevices.SingleAsync();
        device.ConnectivityStatus = "OFFLINE"; device.LastSeenAt = null; await db.SaveChangesAsync();
        await new HeartbeatEventHandler(db, notifications.Object).HandleAsync($"smartlocker/devices/{topicDevice}/heartbeat",
            "{\"deviceCode\":\"TEST-DEVICE\",\"status\":\"ONLINE\",\"uptime\":120,\"rssi\":-55}", retained, default);
        Assert.Equal("OFFLINE", device.ConnectivityStatus); Assert.Null(device.LastSeenAt);
        notifications.VerifyNoOtherCalls();
    }

    [Fact] public async Task OfflineDeviceNeverReceivesUnlockCommand()
    {
        var device = await db.IoTDevices.SingleAsync(); device.ConnectivityStatus = "OFFLINE";
        await db.SaveChangesAsync();
        Assert.False((await Service.CheckInAsync(Request())).Success);
        mqtt.VerifyNoOtherCalls();
    }

    [Fact] public async Task SignalRFailureAfterUnlockCannotDiscardPendingPhysicalOperation()
    {
        notifications.Setup(x => x.NotifyLockerUnlockedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("Hub unavailable"));
        var result = await Service.CheckInAsync(Request());
        Assert.True(result.Success); Assert.NotNull(booking.CheckedInAt);
        var status = await Service.GetOperationStatusAsync(new() { StationId = booking.StationId, BookingId = booking.Id, SessionToken = result.Data!.SessionToken! });
        Assert.True(status.Success); Assert.False(status.Data!.Complete); Assert.Equal("CHECK_IN", status.Data.Operation);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task EmptyOpenCompartmentCannotBeAssignedToANewDeposit(bool dynamicAllocation)
    {
        locker.DoorState = "OPEN";
        if (dynamicAllocation) { booking.Locker = null; booking.LockerId = null; locker.BusinessStatus = LockerBusinessStatus.AVAILABLE; }
        await db.SaveChangesAsync(); Assert.False((await Service.CheckInAsync(Request())).Success); mqtt.VerifyNoOtherCalls();
    }
    [Fact] public async Task AssignedCompartmentCannotBelongToAnotherActiveBooking()
    {
        db.Bookings.Add(new Booking { Id = Guid.NewGuid(), LockerId = locker.Id, StationId = booking.StationId,
            Status = BookingStatus.STORED, BookingCode = "UNIT-OTHER" });
        await db.SaveChangesAsync(); Assert.False((await Service.CheckInAsync(Request())).Success); mqtt.VerifyNoOtherCalls();
    }
    public void Dispose() => db.Dispose();
}
