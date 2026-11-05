using Application.DTOs.Admin;
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
public sealed class LockerGridTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<ILockerNotificationService> notifications = new();
    private AdminStationLockerService Service => new(db, Mock.Of<IAuditLogService>(), notifications.Object);
    private readonly Station station = new() { Id = Guid.NewGuid(), Name = "UNIT-GRID" };
    private Locker Add(LockerSize size, LockerBusinessStatus status = LockerBusinessStatus.AVAILABLE) {
        var l = new Locker { Id = Guid.NewGuid(), StationId = station.Id, Station = station, Size = size, LockerCode = "UNIT-" + size, BusinessStatus = status, DoorState = "CLOSED" }; db.Add(l); return l;
    }
    public LockerGridTests() { db.Add(station); }
    [Fact] public async Task GridKeepsExactFourBusinessStatesAndNeverClaimsUnmappedOrStaleDevicesOnline() {
        var s = Add(LockerSize.S, LockerBusinessStatus.RESERVED); var m = Add(LockerSize.M, LockerBusinessStatus.OCCUPIED);
        var l = Add(LockerSize.L, LockerBusinessStatus.MAINTENANCE); Add(LockerSize.S);
        var device = new IoTDevice { Id = Guid.NewGuid(), StationId = station.Id, ConnectivityStatus = "ONLINE", LastSeenAt = DateTime.UtcNow.AddMinutes(-3) };
        m.IoTDeviceId = device.Id; db.Add(device); await db.SaveChangesAsync();
        var result = (await Service.GetStationLockersGridAsync(station.Id)).Data!;
        Assert.Contains(result, x => x.BusinessStatus == "AVAILABLE"); Assert.Contains(result, x => x.BusinessStatus == "RESERVED");
        Assert.Contains(result, x => x.BusinessStatus == "OCCUPIED"); Assert.Contains(result, x => x.BusinessStatus == "MAINTENANCE");
        Assert.Equal("UNMAPPED", result.Single(x => x.Id == s.Id).ConnectivityStatus);
        Assert.Equal("OFFLINE", result.Single(x => x.Id == m.Id).ConnectivityStatus);
        Assert.Equal("UNKNOWN", (await Service.GetStationLockersGridAsync(Guid.NewGuid())).Data!.FirstOrDefault()?.ConnectivityStatus ?? "UNKNOWN");
    }
    [Theory] [InlineData(BookingStatus.CONFIRMED)] [InlineData(BookingStatus.STORED)] [InlineData(BookingStatus.PENDING_PAYMENT)]
    public async Task StatusControlsCannotFreeOrReclassifyAnActiveBooking(BookingStatus status) {
        var locker = Add(LockerSize.L, LockerBusinessStatus.OCCUPIED);
        db.Add(new Booking { Id = Guid.NewGuid(), StationId = station.Id, LockerId = locker.Id, Status = status }); await db.SaveChangesAsync();
        var result = await Service.UpdateLockerStatusAsync(locker.Id, new() { BusinessStatus = LockerBusinessStatus.AVAILABLE, HealthStatus = LockerHealthStatus.HEALTHY, Reason = "UNIT repaired" }, Guid.NewGuid());
        Assert.False(result.Success); Assert.Equal(LockerBusinessStatus.OCCUPIED, locker.BusinessStatus);
    }
    [Theory] [InlineData(LockerBusinessStatus.OCCUPIED)] [InlineData(LockerBusinessStatus.RESERVED)] [InlineData((LockerBusinessStatus)99)]
    public async Task AdministrativeControlsCannotFabricateBookingStatus(LockerBusinessStatus status) {
        var locker = Add(LockerSize.S); await db.SaveChangesAsync();
        Assert.False((await Service.UpdateLockerStatusAsync(locker.Id, new() { BusinessStatus = status, Reason = "UNIT reason" }, Guid.NewGuid())).Success);
        Assert.Equal(LockerBusinessStatus.AVAILABLE, locker.BusinessStatus);
    }
    [Fact] public async Task MaintenancePublishesAfterSaveAndCannotBecomeAvailableWhileDoorUnknown() {
        var locker = Add(LockerSize.M); await db.SaveChangesAsync();
        Assert.True((await Service.UpdateLockerStatusAsync(locker.Id, new() { BusinessStatus = LockerBusinessStatus.MAINTENANCE, HealthStatus = LockerHealthStatus.WARNING, Reason = "UNIT inspection" }, Guid.NewGuid())).Success);
        notifications.Verify(n => n.NotifyLockerStatusChangedAsync(station.Id, locker.Id), Times.Once);
        locker.DoorState = "UNKNOWN"; await db.SaveChangesAsync();
        Assert.False((await Service.UpdateLockerStatusAsync(locker.Id, new() { BusinessStatus = LockerBusinessStatus.AVAILABLE, Reason = "UNIT repaired" }, Guid.NewGuid())).Success);
    }
    [Fact] public async Task KioskAndAdminUseSameActualReservationsWithoutAssigningAnArbitraryCompartment() {
        var locker = Add(LockerSize.M);
        var unassigned = new Booking { Id = Guid.NewGuid(), StationId = station.Id, Size = LockerSize.M, Status = BookingStatus.CONFIRMED };
        db.AddRange(unassigned,
            new Booking { Id = Guid.NewGuid(), StationId = station.Id, Size = LockerSize.L, Status = BookingStatus.PENDING_PAYMENT, PaymentExpiresAt = DateTime.UtcNow.AddMinutes(10) },
            new Booking { Id = Guid.NewGuid(), StationId = station.Id, Size = LockerSize.S, Status = BookingStatus.PENDING_PAYMENT, PaymentExpiresAt = DateTime.UtcNow.AddMinutes(-1) },
            new Booking { Id = Guid.NewGuid(), StationId = Guid.NewGuid(), Size = LockerSize.M, Status = BookingStatus.CONFIRMED }); await db.SaveChangesAsync();
        var kiosk = new KioskService(db, Mock.Of<IMqttPublishService>(), notifications.Object, Mock.Of<IPricingService>(), NullLogger<KioskService>.Instance);
        var actual = (await kiosk.GetStationStatusAsync(station.Id)).Data!.Reservations;
        var admin = await Service.GetStationReservationsAsync(station.Id);
        Assert.Equal(1, actual.Single(x => x.Size == "M").UnassignedReservations);
        Assert.Equal(1, actual.Single(x => x.Size == "L").PendingPayment); Assert.Equal(0, actual.Single(x => x.Size == "S").PendingPayment);
        Assert.Equal(admin.Select(x => (x.Size,x.AwaitingDeposit,x.PendingPayment,x.Stored)), actual.Select(x => (x.Size,x.AwaitingDeposit,x.PendingPayment,x.Stored)));
        Assert.Null(unassigned.LockerId); Assert.Equal(LockerBusinessStatus.AVAILABLE, locker.BusinessStatus);
        unassigned.LockerId = locker.Id; unassigned.Status = BookingStatus.STORED; locker.BusinessStatus = LockerBusinessStatus.OCCUPIED; await db.SaveChangesAsync();
        actual = (await kiosk.GetStationStatusAsync(station.Id)).Data!.Reservations;
        Assert.Equal(0, actual.Single(x => x.Size == "M").UnassignedReservations); Assert.Equal(1, actual.Single(x => x.Size == "M").Stored);
        unassigned.Status = BookingStatus.COMPLETED; await db.SaveChangesAsync();
        Assert.Equal(0, (await Service.GetStationReservationsAsync(station.Id)).Single(x => x.Size == "M").Stored);
    }
    [Fact] public async Task HeartbeatResynchronizesOnlyAnUnambiguousIdleBayWithoutCreatingDoorTransitionEvidence() {
        var idle = Add(LockerSize.M); idle.DoorState="OPEN"; idle.LastDoorEventAt=DateTime.UtcNow.AddDays(-2);
        var busy = Add(LockerSize.S,LockerBusinessStatus.OCCUPIED);busy.DoorState="OPEN";
        var duplicate1=Add(LockerSize.L);var duplicate2=Add(LockerSize.L);duplicate1.DoorState=duplicate2.DoorState="OPEN";
        var device=new IoTDevice{Id=Guid.NewGuid(),StationId=station.Id,DeviceCode="UNIT-SNAPSHOT",LastSeenAt=DateTime.UtcNow};
        foreach(var l in new[]{idle,busy,duplicate1,duplicate2})l.IoTDeviceId=device.Id;
        var order=new Booking{Id=Guid.NewGuid(),StationId=station.Id,LockerId=busy.Id,Status=BookingStatus.STORED,CheckedOutAt=DateTime.UtcNow.AddSeconds(-10)};
        db.AddRange(device,order);await db.SaveChangesAsync();var lastRealAt=idle.LastDoorEventAt;
        await new Infrastructure.Mqtt.HeartbeatEventHandler(db,notifications.Object).HandleAsync("smartlocker/devices/UNIT-SNAPSHOT/heartbeat",
            "{\"deviceCode\":\"UNIT-SNAPSHOT\",\"status\":\"ONLINE\",\"uptime\":120,\"rssi\":-55,\"doorS\":\"CLOSED\",\"doorM\":\"CLOSED\",\"doorL\":\"CLOSED\"}",false,default);
        Assert.Equal("CLOSED",idle.DoorState);Assert.Equal(lastRealAt,idle.LastDoorEventAt);
        Assert.Equal("OPEN",busy.DoorState);Assert.Equal(BookingStatus.STORED,order.Status);
        Assert.Equal("OPEN",duplicate1.DoorState);Assert.Equal("OPEN",duplicate2.DoorState);
        notifications.Verify(n=>n.NotifyStationStatusChangedAsync(station.Id,"ONLINE",It.IsAny<DateTimeOffset>()),Times.Once);
    }
    public void Dispose() => db.Dispose();
}
