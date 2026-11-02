using System.Security.Claims;
using API.Controllers;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Hung.UseCases.Tests;

// Isolated tests; real payment/device acceptance is recorded separately.
public class TicketTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IAccessPinProtector> protector = new();
    private readonly Mock<IPaymentGatewayService> gateway = new();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Booking booking;
    private readonly OnlineTicketController controller;
    private readonly OfflineTicketService offline;
    public TicketTests()
    {
        protector.Setup(x => x.Protect(It.IsAny<Guid>(), It.IsAny<string>())).Returns<Guid, string>((_, pin) => "unit-cipher:" + pin);
        protector.Setup(x => x.Unprotect(It.IsAny<Guid>(), It.IsAny<string>())).Returns<Guid, string>((_, cipher) => cipher.StartsWith("unit-cipher:") ? cipher[12..] : null);
        var station = new Station { Id = Guid.NewGuid(), Name = "UNIT station", Status = StationStatus.ACTIVE };
        booking = new Booking { Id = Guid.NewGuid(), UserId = owner, BookingCode = "UNIT-T09", Station = station,
            StationId = station.Id, Status = BookingStatus.CONFIRMED, StartAt = DateTime.UtcNow, EndAt = DateTime.UtcNow.AddHours(1) };
        booking.AccessCredential = new AccessCredential { Id = Guid.NewGuid(), BookingId = booking.Id,
            AccessCodeHash = OnlineTicket.HashPin("123456"), OfflinePayload = "123456", OfflineNonce = "unit-old-nonce", IsActive = true };
        booking.Payments.Add(new Payment { Id = Guid.NewGuid(), BookingId = booking.Id, Kind = PaymentKind.BASE,
            Status = PaymentStatus.PAID, PaidAt = DateTime.UtcNow, OrderCode = "123456789", Amount = 15000 });
        db.Add(booking); db.SaveChanges();
        gateway.Setup(x => x.GetPaymentStatusAsync(123456789)).ReturnsAsync("PAID");
        controller = new(db, protector.Object, gateway.Object) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", owner.ToString())], "Unit")) } } };
        offline = new(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["JwtSettings:SecretKey"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") }).Build(),
            NullLogger<OfflineTicketService>.Instance, protector.Object);
    }

    [Fact] public async Task ExplicitLegacyUpgradeKeepsPaidBookingAndIsIdempotent()
    {
        var paidAt = booking.Payments.Single().PaidAt;
        Assert.IsType<NoContentResult>(await controller.Upgrade(booking.Id, new(true)));
        var c = booking.AccessCredential!; var cipher = c.ProtectedAccessCode; var qr = c.QrPayload;
        Assert.Matches("^[0-9]{8}$", OnlineTicket.GetDisplayPin(c, protector.Object)!);
        Assert.NotEqual(OnlineTicket.HashPin("123456"), c.AccessCodeHash);
        Assert.NotEqual("unit-old-nonce", c.OfflineNonce);
        Assert.False(OnlineTicket.NeedsRotation(c, booking.StationId, DateTime.UtcNow));
        Assert.Equal(BookingStatus.CONFIRMED, booking.Status); Assert.Null(booking.CheckedInAt);
        Assert.Equal(paidAt, booking.Payments.Single().PaidAt); Assert.Equal(15000, booking.Payments.Single().Amount);
        Assert.IsType<NoContentResult>(await controller.Upgrade(booking.Id, new(true)));
        Assert.Equal(cipher, c.ProtectedAccessCode); Assert.Equal(qr, c.QrPayload); Assert.Single(db.AuditLogs);
    }
    [Fact] public async Task UpgradeRequiresExplicitConsent()
    {
        Assert.IsType<BadRequestObjectResult>(await controller.Upgrade(booking.Id, new(false)));
        Assert.Null(booking.AccessCredential!.ProtectedAccessCode); Assert.Empty(db.AuditLogs);
    }
    [Fact] public async Task AnotherAccountCannotReplaceOwnerTicket()
    {
        controller.HttpContext.User = new(new ClaimsIdentity([new Claim("uid", Guid.NewGuid().ToString())], "Unit"));
        Assert.IsType<NotFoundResult>(await controller.Upgrade(booking.Id, new(true)));
        Assert.Null(booking.AccessCredential!.ProtectedAccessCode);
    }
    [Theory] [InlineData(BookingStatus.STORED)] [InlineData(BookingStatus.PENDING_PAYMENT)]
    [InlineData(BookingStatus.COMPLETED)] [InlineData(BookingStatus.NO_SHOW)]
    public async Task UpgradeCannotChangeAnIneligibleBooking(BookingStatus status)
    {
        booking.Status = status; await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.Upgrade(booking.Id, new(true)));
        Assert.Equal("123456", booking.AccessCredential!.OfflinePayload);
    }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task UpgradeRejectsExpiredArrivalOrUnverifiedPayment(bool expired)
    {
        if (expired) booking.Payments.Single().PaidAt = DateTime.UtcNow.AddMinutes(-31);
        else gateway.Setup(x => x.GetPaymentStatusAsync(123456789)).ReturnsAsync("PENDING");
        await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.Upgrade(booking.Id, new(true)));
        Assert.Null(booking.AccessCredential!.ProtectedAccessCode); Assert.Empty(db.AuditLogs);
    }
    [Fact] public async Task OfflineReadNeverReplacesOnlineQrOrPin()
    {
        await controller.Upgrade(booking.Id, new(true));
        var c = booking.AccessCredential!; var qr = c.QrPayload; var nonce = c.QrNonce; var expires = c.QrExpiresAt;
        var pin = OnlineTicket.GetDisplayPin(c, protector.Object);
        var response = await offline.GetOfflineTicketAsync(owner, booking.Id);
        Assert.True(response.Success); Assert.Equal(pin, response.Data!.AccessPin);
        Assert.Equal(qr, c.QrPayload); Assert.Equal(nonce, c.QrNonce); Assert.Equal(expires, c.QrExpiresAt);
        Assert.True(offline.VerifyHmacToken(response.Data.QrData).IsValid);
        Assert.NotEqual(qr, response.Data.QrData);
    }
    [Theory] [InlineData(BookingStatus.COMPLETED)] [InlineData(BookingStatus.COMPLETED_MANUAL)]
    [InlineData(BookingStatus.NO_SHOW)] [InlineData(BookingStatus.PENDING_PAYMENT)]
    public async Task OfflineReadCannotCreateCredentialsForEndedOrUnpaidBookings(BookingStatus status)
    {
        booking.Status = status; await db.SaveChangesAsync();
        Assert.False((await offline.GetOfflineTicketAsync(owner, booking.Id)).Success);
        Assert.Single(db.AccessCredentials); Assert.Equal("123456", booking.AccessCredential!.OfflinePayload);
    }
    [Fact] public async Task VerifiedPaymentIssuesOneProtectedPinAndSeparateTickets()
    {
        db.Remove(booking.AccessCredential!); booking.AccessCredential = null;
        booking.Status = BookingStatus.PENDING_PAYMENT; booking.Payments.Single().Status = PaymentStatus.PENDING;
        await db.SaveChangesAsync();
        var service = new PaymentService(db, gateway.Object, offline, NullLogger<PaymentService>.Instance, protector.Object);
        Assert.True((await service.SyncPaymentStatusAsync(123456789)).Data!.IsSuccess);
        var c = await db.AccessCredentials.SingleAsync(); var cipher = c.ProtectedAccessCode;
        var pin = OnlineTicket.GetDisplayPin(c, protector.Object);
        Assert.Matches("^[0-9]{8}$", pin!); Assert.False(string.IsNullOrEmpty(c.OfflineNonce));
        Assert.True(offline.VerifyHmacToken(c.OfflinePayload!).IsValid);
        Assert.False(OnlineTicket.NeedsRotation(c, booking.StationId, DateTime.UtcNow));
        Assert.NotEqual(c.QrPayload, c.OfflinePayload);
        await service.SyncPaymentStatusAsync(123456789);
        Assert.Single(db.AccessCredentials); Assert.Equal(cipher, c.ProtectedAccessCode);
    }
    [Fact] public async Task TicketReadRotatesLegacyLongLivedQrWithoutChangingPin()
    {
        await controller.Upgrade(booking.Id, new(true));
        var c = booking.AccessCredential!; var cipher = c.ProtectedAccessCode;
        c.QrPayload = "legacy-ticket"; c.QrExpiresAt = DateTime.UtcNow.AddHours(3); await db.SaveChangesAsync();
        var service = new BookingService(db, Mock.Of<IPricingService>(), gateway.Object, Mock.Of<IPaymentService>(), offline,
            NullLogger<BookingService>.Instance, pinProtector: protector.Object);
        var response = await service.GetBookingByIdAsync(owner, booking.Id);
        Assert.True(response.Success); Assert.NotEqual("legacy-ticket", response.Data!.QrPayload);
        Assert.Equal(cipher, c.ProtectedAccessCode); Assert.False(OnlineTicket.NeedsRotation(c, booking.StationId, DateTime.UtcNow));
    }
    public void Dispose() => db.Dispose();
}
