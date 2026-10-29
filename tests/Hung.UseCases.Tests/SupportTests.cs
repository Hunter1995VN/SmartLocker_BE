using System.Security.Claims;
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

public class SupportTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid owner = Guid.NewGuid();
    private readonly Booking booking;
    private readonly TravelerSupportController controller;
    private static ControllerContext Context(Guid user) => new() { HttpContext = new DefaultHttpContext {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", user.ToString()), new Claim(ClaimTypes.Role, "STAFF")], "Test")) } };
    public SupportTests()
    {
        booking = new Booking { Id = Guid.NewGuid(), UserId = owner, StationId = Guid.NewGuid(), Status = BookingStatus.COMPLETED,
            EndAt = DateTime.UtcNow.AddHours(2), CheckedOutAt = DateTime.UtcNow,
            Locker = new Locker { Id = Guid.NewGuid(), BusinessStatus = LockerBusinessStatus.AVAILABLE } };
        booking.LockerId = booking.Locker.Id; db.Add(booking); db.SaveChanges();
        controller = new TravelerSupportController(db) { ControllerContext = Context(owner) };
    }
    [Fact] public async Task OtherCustomerCannotReadSupport()
    {
        controller.ControllerContext = Context(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(await controller.Get(booking.Id));
    }
    [Theory] [InlineData(0)] [InlineData(6)]
    public async Task InvalidStarsRejected(int stars) => Assert.IsType<BadRequestObjectResult>(await controller.Review(booking.Id, new(stars, "", [])));
    [Fact] public async Task ReviewOncePerCompletedBooking()
    {
        Assert.IsType<OkObjectResult>(await controller.Review(booking.Id, new(5, "Good", ["CLEAN"])));
        Assert.IsType<ConflictObjectResult>(await controller.Review(booking.Id, new(4, "Again", [])));
        Assert.Single(db.StationReviews);
    }
    [Fact] public async Task StoredBookingCannotBeReviewed()
    {
        booking.Status = BookingStatus.STORED; await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.Review(booking.Id, new(5, "", [])));
    }
    [Fact] public async Task LostReportHasSevenDayLimit()
    {
        booking.CheckedOutAt = DateTime.UtcNow.AddDays(-8); await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await controller.ReportLost(booking.Id, new("Jacket", null)));
    }
    [Fact] public async Task LostReportMustNotBlockNextCustomersOccupiedLocker()
    {
        booking.Locker!.BusinessStatus = LockerBusinessStatus.OCCUPIED; await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await controller.ReportLost(booking.Id, new("Jacket", null)));
        Assert.Equal(LockerBusinessStatus.OCCUPIED, booking.Locker.BusinessStatus);
    }
    [Fact] public async Task LostReportBlocksEmptyLockerAndRejectsDuplicate()
    {
        Assert.IsType<OkObjectResult>(await controller.ReportLost(booking.Id, new("Jacket", null)));
        Assert.Equal(LockerBusinessStatus.BLOCKED, booking.Locker!.BusinessStatus);
        Assert.IsType<ConflictObjectResult>(await controller.ReportLost(booking.Id, new("Jacket", null)));
    }
    [Fact] public async Task DelegationReplacesOldGrantAndNeverStoresPlainPin()
    {
        booking.Status = BookingStatus.STORED; booking.CheckedOutAt = null; await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await controller.Delegate(booking.Id, new("friend@example.invalid", 60)));
        Assert.IsType<OkObjectResult>(await controller.Delegate(booking.Id, new("other@example.invalid", 30)));
        Assert.Single(db.PickupDelegations.Where(x => x.RevokedAt == null));
        Assert.All(db.PickupDelegations, x => Assert.Equal(44, x.PinHash.Length));
    }
    [Fact] public async Task ManualApprovalRequiresPendingEvidence()
    {
        var staff = new ManualConfirmController(db) { ControllerContext = Context(owner) };
        Assert.IsType<BadRequestObjectResult>(await staff.Approve(booking.Id, new("Verified")));
        booking.Status = BookingStatus.STORED; booking.ManualConfirmPhotoUrl = Guid.NewGuid().ToString("N") + ".jpg";
        await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await staff.Approve(booking.Id, new("Verified empty locker and door")));
        Assert.Equal(BookingStatus.COMPLETED, booking.Status);
        Assert.Equal(LockerBusinessStatus.MAINTENANCE, booking.Locker!.BusinessStatus);
        Assert.Single(db.AuditLogs);
    }
    public void Dispose() => db.Dispose();
}
