using API.Controllers;
using Application.DTOs.Common;
using Application.DTOs.Kiosk;
using Application.DTOs.Station;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Mqtt;
using Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;
using Xunit;

namespace Hung.UseCases.Tests;

// Isolated software tests. These events never reach MQTT, hardware, or the shared SQL DB.
public sealed class MultiSizeLockerTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IMqttPublishService> mqtt = new();
    private readonly Mock<ILockerNotificationService> notifications = new();
    private readonly Mock<IPricingService> pricing = new();
    private readonly Station station = new() { Id = Guid.NewGuid(), Name = "UNIT-3-BAY", TotalS = 10, TotalM = 8, TotalL = 4 };
    private readonly IoTDevice device;
    private readonly List<Locker> lockers = [];
    private KioskService Kiosk => new(db, mqtt.Object, notifications.Object, pricing.Object, NullLogger<KioskService>.Instance);

    public MultiSizeLockerTests()
    {
        device = new() { Id = Guid.NewGuid(), StationId = station.Id, DeviceCode = "UNIT-ESP32", LastSeenAt = DateTime.UtcNow };
        db.AddRange(station, device);
        foreach (var size in Enum.GetValues<LockerSize>())
        {
            var locker = new Locker { Id = Guid.NewGuid(), StationId = station.Id, Station = station,
                IoTDeviceId = device.Id, LockerCode = $"UNIT-{size}-01", Size = size, DoorState = "CLOSED" };
            lockers.Add(locker); db.Add(locker);
            db.Add(new PricingPolicy { Id = Guid.NewGuid(), StationId = station.Id, Size = size,
                PricePerBlock = 25000, BlockHours = 2, MinHours = 1, MaxHours = 24, EffectiveFrom = DateTime.UtcNow.AddDays(-1) });
            pricing.Setup(p => p.GetActivePolicyAsync(station.Id, size)).ReturnsAsync(new PricingPolicy());
        }
        mqtt.Setup(m => m.PublishUnlockCommandAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), 500)).ReturnsAsync(true);
        db.SaveChanges();
    }

    private Booking PaidBooking(LockerSize size)
    {
        var booking = new Booking { Id = Guid.NewGuid(), StationId = station.Id, Station = station,
            BookingCode = $"UNIT-{size}", Size = size, Status = BookingStatus.CONFIRMED,
            StartAt = DateTime.UtcNow, EndAt = DateTime.UtcNow.AddHours(1) };
        booking.AccessCredential = new() { Id = Guid.NewGuid(), BookingId = booking.Id, IsActive = true,
            AccessCodeHash = OnlineTicket.HashPin("01234567") };
        OnlineTicket.Rotate(booking.AccessCredential, station.Id, DateTime.UtcNow);
        booking.Payments.Add(new() { Id = Guid.NewGuid(), BookingId = booking.Id, Kind = PaymentKind.BASE,
            Status = PaymentStatus.PAID, PaidAt = DateTime.UtcNow });
        db.Add(booking); db.SaveChanges(); return booking;
    }

    private Task Door(Locker locker, string state, DateTimeOffset at) =>
        new DoorEventHandler(db, notifications.Object, NullLogger<DoorEventHandler>.Instance).HandleAsync(
            "smartlocker/devices/UNIT-ESP32/door",
            JsonSerializer.Serialize(new DoorEvent(1, Guid.NewGuid(), device.DeviceCode, locker.Id, state, at),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), false, default);

    [Theory] [InlineData(LockerSize.S)] [InlineData(LockerSize.M)] [InlineData(LockerSize.L)]
    public async Task DepositAndPickupOperateOnlyTheCorrectSizeAndAssignedCompartment(LockerSize size)
    {
        var booking = PaidBooking(size);
        var target = lockers.Single(l => l.Size == size);
        var other = lockers.First(l => l.Size != size);
        var request = new KioskCheckInRequest { StationId = station.Id, CredentialType = "ACCESS_CODE", Payload = "01234567" };
        var deposit = await Kiosk.CheckInAsync(request);
        Assert.True(deposit.Success); Assert.Equal(target.Id, deposit.Data!.LockerId);
        Assert.Equal(size.ToString(), deposit.Data.LockerSize);
        var at = DateTimeOffset.UtcNow.AddMilliseconds(20);
        await Door(other, "OPEN", at); await Door(other, "CLOSED", at.AddMilliseconds(10));
        Assert.Equal(BookingStatus.CONFIRMED, booking.Status);
        await Door(target, "OPEN", at.AddMilliseconds(20)); await Door(target, "CLOSED", at.AddMilliseconds(30));
        Assert.Equal(BookingStatus.STORED, booking.Status);
        var pickup = await Kiosk.CheckOutAsync(new() { StationId = station.Id, CredentialType = "QR", Payload = booking.AccessCredential!.QrPayload });
        Assert.True(pickup.Success); Assert.Equal(target.Id, pickup.Data!.LockerId);
        // Anchor pickup events to its own command, including slow first-run EF/JIT execution.
        var pickupAt = new DateTimeOffset(DateTime.SpecifyKind(booking.CheckedOutAt!.Value, DateTimeKind.Utc)).AddMilliseconds(20);
        if (pickupAt <= at.AddMilliseconds(30)) pickupAt = at.AddMilliseconds(40);
        await Door(other, "OPEN", pickupAt); await Door(other, "CLOSED", pickupAt.AddMilliseconds(10));
        Assert.Equal(BookingStatus.STORED, booking.Status);
        await Door(target, "OPEN", pickupAt.AddMilliseconds(20)); await Door(target, "CLOSED", pickupAt.AddMilliseconds(30));
        Assert.Equal(BookingStatus.COMPLETED, booking.Status);
        Assert.Equal(LockerBusinessStatus.AVAILABLE, target.BusinessStatus);
        Assert.False(booking.AccessCredential.IsActive);
        Assert.All(lockers.Where(l => l.Id != target.Id), l => Assert.Equal(LockerBusinessStatus.AVAILABLE, l.BusinessStatus));
        mqtt.Verify(m => m.PublishUnlockCommandAsync(device.DeviceCode, target.Id, target.LockerCode, It.IsAny<string?>(), 500), Times.Exactly(2));
        mqtt.VerifyNoOtherCalls();
    }

    [Theory] [InlineData(LockerSize.S)] [InlineData(LockerSize.M)] [InlineData(LockerSize.L)]
    public async Task UnavailableRequestedSizeCannotFallBackToAnotherSize(LockerSize size)
    {
        PaidBooking(size);
        lockers.Single(l => l.Size == size).IoTDeviceId = null; await db.SaveChangesAsync();
        Assert.False((await Kiosk.CheckInAsync(new() { StationId = station.Id, CredentialType = "ACCESS_CODE", Payload = "01234567" })).Success);
        mqtt.VerifyNoOtherCalls();
    }

    [Theory] [InlineData("UNMAPPED")] [InlineData("OFFLINE")] [InlineData("STALE")] [InlineData("OPEN")] [InlineData("MAINTENANCE")] [InlineData("WRONG_STATION")]
    public async Task InventoryExcludesCompartmentsThatCannotBeUsed(string reason)
    {
        var locker = lockers.Single(l => l.Size == LockerSize.M);
        if (reason == "UNMAPPED") locker.IoTDeviceId = null;
        if (reason == "OFFLINE") device.ConnectivityStatus = "OFFLINE";
        if (reason == "STALE") device.LastSeenAt = DateTime.UtcNow.AddMinutes(-3);
        if (reason == "OPEN") locker.DoorState = "OPEN";
        if (reason == "MAINTENANCE") locker.BusinessStatus = LockerBusinessStatus.MAINTENANCE;
        if (reason == "WRONG_STATION") device.StationId = Guid.NewGuid();
        await db.SaveChangesAsync(); var now = DateTime.UtcNow;
        Assert.Equal(0, await LockerAvailability.CountAsync(db, station.Id, LockerSize.M, now, now.AddHours(1), now));
    }

    [Fact] public async Task AssignedReservationsAreNotSubtractedTwiceAndSizesRemainIndependent()
    {
        var booked = PaidBooking(LockerSize.M); booked.LockerId = lockers.Single(l => l.Size == LockerSize.M).Id;
        booked.Locker = lockers.Single(l => l.Size == LockerSize.M); booked.Locker.BusinessStatus = LockerBusinessStatus.RESERVED;
        db.Lockers.Add(new() { Id = Guid.NewGuid(), StationId = station.Id, Size = LockerSize.M,
            IoTDeviceId = device.Id, DoorState = "CLOSED", LockerCode = "UNIT-M-02" });
        await db.SaveChangesAsync(); var now = DateTime.UtcNow;
        Assert.Equal(1, await LockerAvailability.CountAsync(db, station.Id, LockerSize.M, now, now.AddHours(1), now));
        Assert.Equal(1, await LockerAvailability.CountAsync(db, station.Id, LockerSize.L, now, now.AddHours(1), now));
    }

    [Fact] public async Task OnlyLiveUnassignedReservationsConsumeSizeCapacity()
    {
        var booking = PaidBooking(LockerSize.L); var now = DateTime.UtcNow;
        Assert.Equal(0, await LockerAvailability.CountAsync(db, station.Id, LockerSize.L, now, now.AddHours(1), now));
        Assert.Equal(1, await LockerAvailability.CountAsync(db, station.Id, LockerSize.M, now, now.AddHours(1), now));
        booking.Status = BookingStatus.PENDING_PAYMENT; booking.PaymentExpiresAt = now.AddSeconds(-1); await db.SaveChangesAsync();
        Assert.Equal(1, await LockerAvailability.CountAsync(db, station.Id, LockerSize.L, now, now.AddHours(1), now));
    }

    [Fact] public async Task PublicStationUsesMappedInventoryAndRealBlockPricing()
    {
        db.Lockers.Add(new() { Id = Guid.NewGuid(), StationId = station.Id, Size = LockerSize.M, LockerCode = "UNMAPPED-M", DoorState = "CLOSED" });
        await db.SaveChangesAsync();
        var response = Assert.IsType<OkObjectResult>(await new StationsController(db).GetStation(station.Id));
        var dto = Assert.IsType<ApiResponse<StationDto>>(response.Value).Data!;
        Assert.Equal(1, dto.TotalS); Assert.Equal(1, dto.TotalM); Assert.Equal(1, dto.TotalL);
        Assert.Equal(3, dto.Lockers.Count); Assert.Equal(1, dto.AvailableM);
        Assert.All(dto.SizePricing, p => { Assert.Equal(2, p.BlockHours); Assert.Equal(25000, p.PricePerBlock); });
        var listResult = Assert.IsType<OkObjectResult>(await new StationsController(db).GetStations());
        var list = Assert.IsType<ApiResponse<List<StationListItemDto>>>(listResult.Value).Data!;
        var summary = Assert.Single(list);
        Assert.Equal(dto.TotalM, summary.TotalM); Assert.Equal(dto.AvailableM, summary.AvailableM);
    }
    [Fact] public async Task StationSearchPreservesGpsFilteringWithoutSeedingInventoryOnReads()
    {
        station.Latitude = 10.7725m; station.Longitude = 106.698m;
        db.Stations.Add(new() { Id = Guid.NewGuid(), Name = "UNIT-FAR", Latitude = 16.0538m, Longitude = 108.2036m });
        await db.SaveChangesAsync();
        var stationCount = await db.Stations.CountAsync();
        var lockerCount = await db.Lockers.CountAsync();
        var policyCount = await db.PricingPolicies.CountAsync();
        var response = Assert.IsType<OkObjectResult>(await new StationsController(db).GetStations(
            userLat: 10.7725, userLng: 106.698, radiusKm: 1));
        var result = Assert.IsType<ApiResponse<List<StationListItemDto>>>(response.Value).Data!;
        var nearby = Assert.Single(result);
        Assert.Equal(station.Id, nearby.Id); Assert.Equal(0d, nearby.DistanceKm!.Value);
        Assert.Equal(stationCount, await db.Stations.CountAsync());
        Assert.Equal(lockerCount, await db.Lockers.CountAsync());
        Assert.Equal(policyCount, await db.PricingPolicies.CountAsync());
    }
    public void Dispose() => db.Dispose();
}
