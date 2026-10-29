namespace Hung.UseCases.Tests;

using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

public class TicketTests : IDisposable
{
    private readonly SmartLockerDbContext _db;
    private readonly Mock<IPricingService> _pricing = new();
    private readonly Mock<IPaymentGatewayService> _gateway = new();
    private readonly Mock<IPaymentService> _paymentService = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Station _station;

    public TicketTests()
    {
        var options = new DbContextOptionsBuilder<SmartLockerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new SmartLockerDbContext(options);

        _station = new Station
        {
            Id = Guid.NewGuid(),
            Name = "Trạm Test Đà Nẵng",
            Address = "123 Nguyễn Văn Linh",
            Status = StationStatus.ACTIVE
        };
        _db.Stations.Add(_station);
        _db.SaveChanges();
    }

    private BookingService CreateBookingService() =>
        new(_db, _pricing.Object, _gateway.Object, _paymentService.Object, NullLogger<BookingService>.Instance);

    [Fact]
    public void UC_T09_TicketRotation_ChangesTokenAndPreservesPinHash()
    {
        // Arrange
        var bookingId = Guid.NewGuid();
        var pin = "08543210";
        var pinHash = OnlineTicket.HashPin(pin);

        var credential = new AccessCredential
        {
            Id = Guid.NewGuid(),
            BookingId = bookingId,
            AccessCodeHash = pinHash,
            OfflinePayload = pin,
            IsActive = true
        };

        var t0 = DateTime.UtcNow;
        OnlineTicket.Rotate(credential, _station.Id, t0);
        var initialPayload = credential.QrPayload;
        var initialNonce = credential.QrNonce;

        // Act
        var t1 = t0.AddSeconds(70);
        OnlineTicket.Rotate(credential, _station.Id, t1);

        // Assert
        Assert.NotEqual(initialPayload, credential.QrPayload);
        Assert.NotEqual(initialNonce, credential.QrNonce);
        Assert.Equal(pinHash, credential.AccessCodeHash);
        Assert.Equal(t1.AddSeconds(60), credential.QrExpiresAt);

        // Verify decoded token structure
        var jsonBytes = Convert.FromBase64String(credential.QrPayload);
        using var doc = JsonDocument.Parse(jsonBytes);
        var root = doc.RootElement;
        Assert.Equal(bookingId.ToString(), root.GetProperty("bookingId").GetString());
        Assert.Equal(_station.Id.ToString(), root.GetProperty("stationId").GetString());
        Assert.Equal(credential.QrNonce, root.GetProperty("nonce").GetString());
    }

    [Fact]
    public void UC_T09_EightDigitPin_IsExactlyEightDigitsNumeric()
    {
        for (int i = 0; i < 50; i++)
        {
            var pin = RandomNumberGenerator.GetInt32(0, 100000000).ToString("D8");
            Assert.Equal(8, pin.Length);
            Assert.All(pin, c => Assert.True(char.IsAsciiDigit(c)));
            Assert.Equal(44, OnlineTicket.HashPin(pin).Length); // Base64 SHA-256 length is 44
        }
    }

    [Fact]
    public async Task UC_T09_GetBookingById_WhenQrExpired_RotatesAndReturnsNewPayload()
    {
        // Arrange
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            BookingCode = "BK-20261029-T091",
            UserId = _userId,
            StationId = _station.Id,
            Station = _station,
            Size = LockerSize.M,
            Status = BookingStatus.CONFIRMED,
            StartAt = DateTime.UtcNow,
            EndAt = DateTime.UtcNow.AddHours(2),
            BaseAmount = 30000
        };

        var expiredTime = DateTime.UtcNow.AddMinutes(-5);
        var credential = new AccessCredential
        {
            Id = Guid.NewGuid(),
            BookingId = booking.Id,
            AccessCodeHash = OnlineTicket.HashPin("12345678"),
            OfflinePayload = "12345678",
            QrPayload = "OLD_EXPIRED_PAYLOAD",
            QrExpiresAt = expiredTime,
            IsActive = true
        };
        booking.AccessCredential = credential;

        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();

        var service = CreateBookingService();

        // Act
        var result = await service.GetBookingByIdAsync(_userId, booking.Id);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.NotEqual("OLD_EXPIRED_PAYLOAD", result.Data.QrPayload);
        Assert.Equal("12345678", result.Data.AccessCode);
        Assert.True(credential.QrExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task UC_T09_GetBookingById_WhenPendingPayment_DoesNotExposeActiveTicket()
    {
        // Arrange
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            BookingCode = "BK-20261029-PENDING",
            UserId = _userId,
            StationId = _station.Id,
            Station = _station,
            Size = LockerSize.S,
            Status = BookingStatus.PENDING_PAYMENT,
            StartAt = DateTime.UtcNow,
            EndAt = DateTime.UtcNow.AddHours(2),
            BaseAmount = 20000
        };
        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();

        var service = CreateBookingService();

        // Act
        var result = await service.GetBookingByIdAsync(_userId, booking.Id);

        // Assert
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Null(result.Data.QrPayload);
        Assert.Null(result.Data.AccessCode);
    }

    [Theory]
    [InlineData(BookingStatus.PENDING_PAYMENT, true)]
    [InlineData(BookingStatus.COMPLETED, true)]
    [InlineData(BookingStatus.CANCELLED, true)]
    [InlineData(BookingStatus.CONFIRMED, false)]
    public async Task IneligibleBookingNeverExposesExistingCredential(BookingStatus status, bool active)
    {
        var booking = new Booking { Id = Guid.NewGuid(), BookingCode = "HIDDEN", UserId = _userId,
            StationId = _station.Id, Station = _station, Status = status, EndAt = DateTime.UtcNow.AddHours(1) };
        booking.AccessCredential = new AccessCredential { Id = Guid.NewGuid(), BookingId = booking.Id,
            IsActive = active, OfflinePayload = "01234567", QrPayload = "secret" };
        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();
        var result = await CreateBookingService().GetBookingByIdAsync(_userId, booking.Id);
        Assert.True(result.Success);
        Assert.Null(result.Data!.AccessCode);
        Assert.Null(result.Data.QrPayload);
    }

    public void Dispose() => _db.Dispose();
}
