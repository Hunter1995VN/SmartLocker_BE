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
    public void Dispose() => db.Dispose();
}
