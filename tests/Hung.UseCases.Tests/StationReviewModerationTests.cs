using System.Security.Claims;
using System.Text.Json;
using API.Controllers;
using API.Controllers.Admin;
using Domain.Entities;
using Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Hung.UseCases.Tests;

public class StationReviewModerationTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid author = Guid.NewGuid();
    private readonly Guid reporter = Guid.NewGuid();
    private readonly Guid admin = Guid.NewGuid();
    private readonly StationReview review;

    public StationReviewModerationTests()
    {
        var station = new Station { Id = Guid.NewGuid(), Name = "Trạm A" };
        var booking = new Booking { Id = Guid.NewGuid(), UserId = author, Station = station, BookingCode = "BK-PRIVATE" };
        review = new StationReview { Id = Guid.NewGuid(), Booking = booking, Stars = 3, Comment = "Nên có thêm biển chỉ dẫn." };
        db.Add(review);
        db.SaveChanges();
    }

    [Fact]
    public async Task ReviewsArePublicByDefaultAndModerationIsReversible()
    {
        var publicApi = new StationReviewsController(db) { ControllerContext = Context() };
        var adminApi = new AdminStationReviewsController(db) { ControllerContext = Context(admin) };
        var initial = Body(await publicApi.List(default));
        var item = Assert.Single(initial.GetProperty("items").EnumerateArray());
        Assert.Equal(3, initial.GetProperty("average").GetDouble());
        Assert.Equal(review.Comment, item.GetProperty("comment").GetString());
        Assert.False(item.TryGetProperty("bookingCode", out _));
        Assert.False(item.TryGetProperty("userId", out _));

        Assert.IsType<NoContentResult>(await adminApi.SetVisibility(review.Id, new(true), default));
        Assert.IsType<NoContentResult>(await adminApi.SetVisibility(review.Id, new(true), default));
        Assert.Empty(Body(await publicApi.List(default)).GetProperty("items").EnumerateArray());
        Assert.Equal(0, Body(await publicApi.List(default)).GetProperty("count").GetInt32());
        Assert.Single(Body(await adminApi.List(default, "hidden")).GetProperty("items").EnumerateArray());
        publicApi.ControllerContext = Context(reporter);
        Assert.IsType<NotFoundObjectResult>(await publicApi.Report(review.Id, new("Spam"), default));

        Assert.IsType<NoContentResult>(await adminApi.SetVisibility(review.Id, new(false), default));
        Assert.Single(Body(await publicApi.List(default)).GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "STATION_REVIEW_HIDE", "STATION_REVIEW_RESTORE" },
            await db.AuditLogs.OrderBy(x => x.Id).Select(x => x.Action).ToArrayAsync());
    }

    [Fact]
    public async Task AnotherAccountCanReportOnceAndAdminCanResolveWithoutHiding()
    {
        var publicApi = new StationReviewsController(db) { ControllerContext = Context() };
        Assert.IsType<UnauthorizedResult>(await publicApi.Report(review.Id, new("Spam"), default));
        publicApi.ControllerContext = Context(author);
        Assert.True(Assert.Single(Body(await publicApi.List(default)).GetProperty("items").EnumerateArray()).GetProperty("isOwn").GetBoolean());
        Assert.IsType<BadRequestObjectResult>(await publicApi.Report(review.Id, new("Spam"), default));

        publicApi.ControllerContext = Context(reporter);
        Assert.IsType<BadRequestObjectResult>(await publicApi.Report(review.Id, new(" "), default));
        Assert.IsType<BadRequestObjectResult>(await publicApi.Report(review.Id, new(new string('a', 501)), default));
        Assert.IsType<NoContentResult>(await publicApi.Report(review.Id, new(" Spam "), default));
        Assert.IsType<NoContentResult>(await publicApi.Report(review.Id, new("Duplicate"), default));
        var report = Assert.Single(db.StationReviewReports);
        Assert.Equal("Spam", report.Reason);
        Assert.True(Assert.Single(Body(await publicApi.List(default)).GetProperty("items").EnumerateArray()).GetProperty("hasReported").GetBoolean());

        var adminApi = new AdminStationReviewsController(db) { ControllerContext = Context(admin) };
        Assert.Single(Body(await adminApi.List(default, "reported")).GetProperty("items").EnumerateArray());
        Assert.Single(Body(await adminApi.Reports(review.Id, default)).GetProperty("items").EnumerateArray());
        Assert.IsType<NotFoundResult>(await adminApi.Resolve(Guid.NewGuid(), report.Id, default));
        Assert.IsType<NoContentResult>(await adminApi.Resolve(review.Id, report.Id, default));
        Assert.IsType<NoContentResult>(await adminApi.Resolve(review.Id, report.Id, default));
        Assert.Empty(Body(await adminApi.List(default, "reported")).GetProperty("items").EnumerateArray());
        Assert.Single(Body(await publicApi.List(default)).GetProperty("items").EnumerateArray());
        Assert.NotNull(report.ResolvedAt);
        Assert.Single(db.AuditLogs);
    }

    private static ControllerContext Context(Guid? userId = null) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(userId is Guid id
            ? new ClaimsIdentity([new Claim("uid", id.ToString())], "Test")
            : new ClaimsIdentity()) }
    };

    private static JsonElement Body(IActionResult result) => JsonSerializer.SerializeToElement(
        Assert.IsType<OkObjectResult>(result).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public void Dispose() => db.Dispose();
}
