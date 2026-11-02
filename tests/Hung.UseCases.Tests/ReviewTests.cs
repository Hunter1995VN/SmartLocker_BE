using System.Security.Claims;
using System.Text.Json;
using API.Controllers;
using API.Controllers.Admin;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hung.UseCases.Tests;

public class ReviewTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid owner = Guid.NewGuid();
    private readonly Booking booking;
    private readonly TravelerSupportController controller;
    private static ControllerContext Identity(Guid user) => new() { HttpContext = new DefaultHttpContext {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", user.ToString())], "Unit")) } };
    public ReviewTests()
    {
        var station = new Station { Id = Guid.NewGuid(), Name = "Unit station" };
        booking = new Booking { Id = Guid.NewGuid(), UserId = owner, StationId = station.Id,
            Station = station, Status = BookingStatus.COMPLETED, CheckedOutAt = DateTime.UtcNow };
        db.Add(booking); db.SaveChanges();
        controller = new TravelerSupportController(db) { ControllerContext = Identity(owner) };
    }
    [Theory] [InlineData(0)] [InlineData(6)]
    public async Task RejectOutOfRangeStars(int stars) => Assert.IsType<BadRequestObjectResult>(
        await controller.Review(booking.Id, new(stars, "", [])));

    [Theory] [InlineData(BookingStatus.COMPLETED)] [InlineData(BookingStatus.COMPLETED_MANUAL)]
    public async Task PersistReviewAfterCheckoutAndRejectDuplicates(BookingStatus status)
    {
        booking.Status = status; await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await controller.Review(booking.Id, new(4, "  Unit feedback  ", ["CLEAN", "FAST", "CLEAN"])));
        var review = Assert.Single(db.StationReviews);
        Assert.Equal("Unit feedback", review.Comment); Assert.Equal("CLEAN,FAST", review.Criteria);
        Assert.IsType<ConflictObjectResult>(await controller.Review(booking.Id, new(3, "Again", [])));
        Assert.Equal(4, Assert.Single(db.StationReviews).Stars);
    }
    [Theory] [InlineData(BookingStatus.STORED)] [InlineData(BookingStatus.CONFIRMED)]
    [InlineData(BookingStatus.CANCELLED)] [InlineData(BookingStatus.PENDING_PAYMENT)]
    public async Task RejectBookingsBeforeActualCompletion(BookingStatus status)
    {
        booking.Status = status; await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.Review(booking.Id, new(5, "", [])));
        Assert.Empty(db.StationReviews);
    }
    [Fact] public async Task CompletionWithoutCheckoutTimestampIsInsufficient()
    {
        booking.CheckedOutAt = null; await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.Review(booking.Id, new(5, "", [])));
    }
    [Fact] public async Task OtherOwnerCannotReadOrSubmit()
    {
        controller.ControllerContext = Identity(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(await controller.Get(booking.Id));
        Assert.IsType<NotFoundResult>(await controller.Review(booking.Id, new(5, "", [])));
        Assert.Empty(db.StationReviews);
    }
    [Fact] public async Task RejectUnsupportedCriteriaAndOversizedComment()
    {
        Assert.IsType<BadRequestObjectResult>(await controller.Review(booking.Id, new(3, "", ["UNKNOWN"])));
        Assert.IsType<BadRequestObjectResult>(await controller.Review(booking.Id, new(3, new string('x', 1001), [])));
        Assert.Empty(db.StationReviews);
    }
    [Fact] public async Task StaffAggregateUsesPersistedStarsAndStation()
    {
        Assert.IsType<OkObjectResult>(await controller.Review(booking.Id, new(2, "One", [])));
        var other = new Booking { Id = Guid.NewGuid(), UserId = owner, StationId = booking.StationId,
            Station = booking.Station, Status = BookingStatus.COMPLETED, CheckedOutAt = DateTime.UtcNow };
        db.Add(other); await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await controller.Review(other.Id, new(4, "Two", ["EASY_TO_FIND"])));
        var response = Assert.IsType<OkObjectResult>(await new TravelerSupportAdminController(db).Ratings());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        var station = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(2, station.GetProperty("count").GetInt32());
        Assert.Equal(3, station.GetProperty("average").GetDouble());
        Assert.Equal(booking.StationId, station.GetProperty("stationId").GetGuid());
    }
    public void Dispose() => db.Dispose();
}
