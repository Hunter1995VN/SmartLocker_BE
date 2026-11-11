using API.Controllers;
using Application.DTOs.Booking;
using Application.DTOs.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Hung.UseCases.Tests;

public class WalkInTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IBookingService> bookings = new();
    private WalkInController Controller => new(db, bookings.Object) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };

    [Theory]
    [InlineData("XL", 3, "0901234567")]
    [InlineData("S", 0, "0901234567")]
    [InlineData("S", 73, "0901234567")]
    [InlineData("S", 3, "bad-phone")]
    public async Task InvalidWalkInNeverCreatesBooking(string size, int hours, string phone)
    {
        Assert.IsType<BadRequestObjectResult>(await Controller.Create(new(Guid.NewGuid(), size, hours, phone)));
        bookings.VerifyNoOtherCalls(); Assert.Empty(db.Users);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task WrongOrExpiredSecretCannotReadGuestTicket(bool expired)
    {
        var secret = new string('A', 64);
        var booking = new Booking { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = BookingStatus.PENDING_PAYMENT };
        var session = new WalkInSession { Id = Guid.NewGuid(), Booking = booking, BookingId = booking.Id,
            SecretHash = OnlineTicket.HashPin(secret), ExpiresAt = DateTime.UtcNow.AddMinutes(expired ? -1 : 30) };
        db.Add(session); await db.SaveChangesAsync();
        Assert.IsType<NotFoundResult>(await Controller.Status(session.Id, new(expired ? secret : new string('B', 64))));
        bookings.VerifyNoOtherCalls();
    }

    [Fact] public async Task GuestStatusUsesStoredOwnerAndPaymentReconciliationService()
    {
        var secret = new string('A', 64);
        var booking = new Booking { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = BookingStatus.PENDING_PAYMENT };
        var session = new WalkInSession { Id = Guid.NewGuid(), Booking = booking, BookingId = booking.Id,
            SecretHash = OnlineTicket.HashPin(secret), ExpiresAt = DateTime.UtcNow.AddMinutes(30) };
        db.Add(session); await db.SaveChangesAsync();
        bookings.Setup(x => x.GetBookingByIdAsync(booking.UserId, booking.Id)).ReturnsAsync(
            ApiResponse<BookingDto>.SuccessResponse(new BookingDto { Id = booking.Id, Status = "PENDING_PAYMENT" }));
        var result = Assert.IsType<OkObjectResult>(await Controller.Status(session.Id, new(secret)));
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("PENDING_PAYMENT", json); Assert.Contains("\"AccessCode\":null", json);
        bookings.Verify(x => x.GetBookingByIdAsync(booking.UserId, booking.Id), Times.Once);
    }
    // ── UC-T15: nhánh hủy đơn chưa thanh toán, ô sẵn sàng và trả PIN ──
    private async Task<(WalkInSession session, string secret, Booking booking)> Session(BookingStatus status, int expiresInMinutes = 30)
    {
        var secret = new string('C', 64);
        var booking = new Booking { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = status };
        var session = new WalkInSession { Id = Guid.NewGuid(), Booking = booking, BookingId = booking.Id,
            SecretHash = OnlineTicket.HashPin(secret), ExpiresAt = DateTime.UtcNow.AddMinutes(expiresInMinutes) };
        db.Add(session); await db.SaveChangesAsync();
        return (session, secret, booking);
    }
    private void StubBooking(Booking booking, string status, string? accessCode = null) =>
        bookings.Setup(x => x.GetBookingByIdAsync(booking.UserId, booking.Id)).ReturnsAsync(
            ApiResponse<BookingDto>.SuccessResponse(new BookingDto { Id = booking.Id, Status = status, AccessCode = accessCode }));

    [Fact] public async Task UnpaidWalkInCanBeCancelledAndSessionEnds()
    {
        var (session, secret, booking) = await Session(BookingStatus.PENDING_PAYMENT);
        StubBooking(booking, "PENDING_PAYMENT");
        bookings.Setup(x => x.CancelBookingAsync(booking.UserId, booking.Id, It.IsAny<CancelBookingRequest>()))
            .ReturnsAsync(ApiResponse<BookingDto>.SuccessResponse(new BookingDto { Id = booking.Id, Status = "CANCELLED" }));
        var ok = Assert.IsType<OkObjectResult>(await Controller.Cancel(session.Id, new(secret, null)));
        Assert.Contains("CANCELLED", System.Text.Json.JsonSerializer.Serialize(ok.Value));
        bookings.Verify(x => x.CancelBookingAsync(booking.UserId, booking.Id, It.Is<CancelBookingRequest>(r => r.Reason!.Contains("Walk-in"))), Times.Once);
        Assert.True((await db.WalkInSessions.FindAsync(session.Id))!.ExpiresAt <= DateTime.UtcNow);
        // Phiên đã kết thúc: không đọc được vé nữa.
        Assert.IsType<NotFoundResult>(await Controller.Status(session.Id, new(secret)));
    }

    [Theory] [InlineData("CONFIRMED")] [InlineData("STORED")] [InlineData("CANCELLED")]
    public async Task PaidOrFinishedWalkInIsNeverCancelledFromKiosk(string status)
    {
        var (session, secret, booking) = await Session(Enum.Parse<BookingStatus>(status));
        StubBooking(booking, status);
        Assert.IsType<ConflictObjectResult>(await Controller.Cancel(session.Id, new(secret, null)));
        bookings.Verify(x => x.CancelBookingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancelBookingRequest>()), Times.Never);
    }

    [Fact] public async Task PaidPaymentBlocksCancelEvenIfStatusLagsBehind()
    {
        var (session, secret, booking) = await Session(BookingStatus.PENDING_PAYMENT);
        db.Payments.Add(new Payment { Id = Guid.NewGuid(), BookingId = booking.Id, Status = PaymentStatus.PAID, Amount = 15000 });
        await db.SaveChangesAsync();
        StubBooking(booking, "PENDING_PAYMENT");
        Assert.IsType<ConflictObjectResult>(await Controller.Cancel(session.Id, new(secret, null)));
        bookings.Verify(x => x.CancelBookingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancelBookingRequest>()), Times.Never);
    }

    [Theory] [InlineData("wrong")] [InlineData("short")] [InlineData("expired")]
    public async Task CancelRequiresTheSessionSecretAndLiveSession(string variant)
    {
        var (session, secret, booking) = await Session(BookingStatus.PENDING_PAYMENT, variant == "expired" ? -1 : 30);
        var attempt = variant switch { "wrong" => new string('D', 64), "short" => "abc", _ => secret };
        var result = await Controller.Cancel(session.Id, new(attempt, null));
        Assert.True(result is NotFoundResult or BadRequestResult);
        bookings.Verify(x => x.CancelBookingAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancelBookingRequest>()), Times.Never);
    }

    [Fact] public async Task ConfirmedGuestStatusReturnsPinOnlyToSecretHolder()
    {
        var (session, secret, booking) = await Session(BookingStatus.CONFIRMED);
        StubBooking(booking, "CONFIRMED", "01234567");
        var ok = Assert.IsType<OkObjectResult>(await Controller.Status(session.Id, new(secret)));
        Assert.Contains("01234567", System.Text.Json.JsonSerializer.Serialize(ok.Value));
        Assert.IsType<NotFoundResult>(await Controller.Status(session.Id, new(new string('E', 64))));
    }

    [Fact] public async Task StationWithoutReadyLockerNeverCreatesGuestOrBooking()
    {
        var station = new Station { Id = Guid.NewGuid(), Name = "Trạm không có ô", Status = StationStatus.ACTIVE };
        db.Stations.Add(station); await db.SaveChangesAsync();
        var bad = Assert.IsType<BadRequestObjectResult>(await Controller.Create(new(station.Id, "S", 3, "0901234567")));
        Assert.Contains("ô tủ phù hợp", System.Text.Json.JsonSerializer.Serialize(bad.Value, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Empty(db.Users); Assert.Empty(db.WalkInSessions);
        bookings.VerifyNoOtherCalls();
    }

    [Theory] [InlineData("guest@example.com")] [InlineData("++++12345678")] [InlineData("1234567")]
    public async Task WalkInRequiresARealPhoneNumber(string phone)
    {
        Assert.IsType<BadRequestObjectResult>(await Controller.Create(new(Guid.NewGuid(), "M", 2, phone)));
        Assert.Empty(db.Users); bookings.VerifyNoOtherCalls();
    }

    public void Dispose() => db.Dispose();
}
