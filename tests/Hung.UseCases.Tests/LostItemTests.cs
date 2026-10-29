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

public class LostItemTests : IDisposable
{
    private readonly SmartLockerDbContext db = new(new DbContextOptionsBuilder<SmartLockerDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid owner = Guid.NewGuid();
    private readonly Booking booking;
    private readonly TravelerSupportController traveler;
    private readonly TravelerSupportAdminController staff;
    private static ControllerContext Identity(Guid user) => new() { HttpContext = new DefaultHttpContext {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", user.ToString())], "Unit")) } };
    public LostItemTests()
    {
        booking = new Booking { Id = Guid.NewGuid(), UserId = owner, StationId = Guid.NewGuid(),
            Status = BookingStatus.COMPLETED, CheckedOutAt = DateTime.UtcNow,
            Locker = new Locker { Id = Guid.NewGuid(), BusinessStatus = LockerBusinessStatus.AVAILABLE,
                HealthStatus = LockerHealthStatus.HEALTHY, DoorState = "CLOSED" } };
        booking.LockerId = booking.Locker.Id; db.Add(booking); db.SaveChanges();
        traveler = new(db) { ControllerContext = Identity(owner) };
        staff = new(db) { ControllerContext = Identity(Guid.NewGuid()) };
    }
    private async Task<LostItemReport> CreateReport()
    {
        Assert.IsType<OkObjectResult>(await traveler.ReportLost(booking.Id, new("Unit illustration, not a real missing item", null)));
        return Assert.Single(db.LostItemReports);
    }
    [Theory] [InlineData(6, true)] [InlineData(8, false)]
    public async Task SevenDayWindowIsEnforced(int days, bool allowed)
    {
        booking.CheckedOutAt = DateTime.UtcNow.AddDays(-days); await db.SaveChangesAsync();
        var response = await traveler.ReportLost(booking.Id, new("Unit", null));
        if (allowed) Assert.IsType<OkObjectResult>(response); else Assert.IsType<BadRequestObjectResult>(response);
        Assert.Equal(allowed ? 1 : 0, db.LostItemReports.Count());
    }
    [Theory] [InlineData(BookingStatus.COMPLETED)] [InlineData(BookingStatus.COMPLETED_MANUAL)]
    public async Task CompletedCheckoutCreatesLinkedPriorityIncident(BookingStatus status)
    {
        booking.Status = status; await db.SaveChangesAsync(); var report = await CreateReport();
        var incident = Assert.Single(db.SecurityIncidents);
        Assert.Equal(booking.Id, incident.BookingId); Assert.Equal(booking.StationId, incident.StationId);
        Assert.Equal("HIGH", incident.Severity); Assert.Equal(IncidentStatus.OPEN, incident.Status);
        Assert.Equal("LF-" + report.Id.ToString("N")[..20], incident.IncidentCode);
        Assert.Equal(LockerBusinessStatus.BLOCKED, booking.Locker!.BusinessStatus);
    }
    [Theory] [InlineData(BookingStatus.STORED)] [InlineData(BookingStatus.CONFIRMED)] [InlineData(BookingStatus.CANCELLED)]
    public async Task CannotReportBeforeCheckout(BookingStatus status)
    {
        booking.Status = status; await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await traveler.ReportLost(booking.Id, new("Unit", null)));
        Assert.Empty(db.LostItemReports);
    }
    [Fact] public async Task MissingCheckoutAndOtherOwnerAreRejected()
    {
        booking.CheckedOutAt = null; await db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await traveler.ReportLost(booking.Id, new("Unit", null)));
        traveler.ControllerContext = Identity(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(await traveler.Get(booking.Id));
        Assert.IsType<NotFoundResult>(await traveler.ReportLost(booking.Id, new("Unit", null)));
    }
    [Theory] [InlineData(BookingStatus.PENDING_PAYMENT)] [InlineData(BookingStatus.CONFIRMED)] [InlineData(BookingStatus.STORED)]
    public async Task NeverBlockNextCustomersActiveLocker(BookingStatus status)
    {
        db.Bookings.Add(new Booking { Id = Guid.NewGuid(), LockerId = booking.LockerId,
            UserId = Guid.NewGuid(), StationId = booking.StationId, Status = status });
        await db.SaveChangesAsync(); await CreateReport();
        Assert.Equal(LockerBusinessStatus.AVAILABLE, booking.Locker!.BusinessStatus);
    }
    [Theory] [InlineData(BookingStatus.PENDING_PAYMENT)] [InlineData(BookingStatus.CONFIRMED)] [InlineData(BookingStatus.STORED)]
    public async Task ResolutionCannotReleaseAnotherActiveReservation(BookingStatus status)
    {
        var report = await CreateReport();
        db.Bookings.Add(new Booking { Id = Guid.NewGuid(), LockerId = booking.LockerId,
            UserId = Guid.NewGuid(), StationId = booking.StationId, Status = status }); await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await staff.Resolve(report.Id, new("NOT_FOUND", "Unit resolution")));
        Assert.Equal(LockerBusinessStatus.BLOCKED, booking.Locker!.BusinessStatus);
    }
    [Fact] public async Task FoundThenReturnedIsIdempotentAuditedAndNotifiesOnlyOwner()
    {
        var report = await CreateReport();
        Assert.IsType<ConflictObjectResult>(await staff.Resolve(report.Id, new("RETURNED", "Unit")));
        Assert.IsType<OkObjectResult>(await staff.Resolve(report.Id, new("FOUND", "Unit found")));
        Assert.IsType<OkObjectResult>(await staff.Resolve(report.Id, new("FOUND", "Unit found")));
        Assert.Single(db.Notifications); Assert.Single(db.AuditLogs);
        Assert.IsType<ConflictObjectResult>(await traveler.ReportLost(booking.Id, new("Duplicate", null)));
        Assert.IsType<OkObjectResult>(await staff.Resolve(report.Id, new("RETURNED", "Unit handed over")));
        Assert.Equal("RETURNED", report.Status); Assert.Equal(2, db.Notifications.Count());
        Assert.Equal(2, db.AuditLogs.Count()); Assert.All(db.Notifications, item => Assert.Equal(owner, item.UserId));
        Assert.IsType<ConflictObjectResult>(await staff.Resolve(report.Id, new("NOT_FOUND", "Cannot reopen terminal handover")));
        Assert.Equal(IncidentStatus.RESOLVED, Assert.Single(db.SecurityIncidents).Status);
        Assert.Equal(LockerBusinessStatus.AVAILABLE, booking.Locker!.BusinessStatus);
        var notice = db.Notifications.First(); traveler.ControllerContext = Identity(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(await traveler.ReadNotification(booking.Id, notice.Id)); Assert.Null(notice.ReadAt);
        traveler.ControllerContext = Identity(owner);
        Assert.IsType<NoContentResult>(await traveler.ReadNotification(booking.Id, notice.Id));
        Assert.IsType<NoContentResult>(await traveler.ReadNotification(booking.Id, notice.Id));
        Assert.Equal("READ", notice.Status); Assert.NotNull(notice.ReadAt);
    }
    [Theory] [InlineData("")] [InlineData(" ")]
    public async Task EmptyDescriptionRejected(string description) => Assert.IsType<BadRequestObjectResult>(
        await traveler.ReportLost(booking.Id, new(description, null)));
    [Theory] [InlineData("data:image/jpeg;base64,not-base64")]
    [InlineData("data:image/jpeg;base64,/9j/2Q==")] [InlineData("data:image/png;base64,/9j/2Q==")]
    public async Task InvalidImageNeverCreatesIncident(string photo)
    {
        Assert.IsType<BadRequestObjectResult>(await traveler.ReportLost(booking.Id, new("Unit", photo)));
        Assert.Empty(db.LostItemReports); Assert.Empty(db.SecurityIncidents);
    }
    [Fact] public async Task OversizedFieldsAndInvalidStaffStatusRejected()
    {
        Assert.IsType<BadRequestObjectResult>(await traveler.ReportLost(booking.Id, new(new string('x', 2001), null)));
        var report = await CreateReport();
        Assert.IsType<BadRequestObjectResult>(await staff.Resolve(report.Id, new("UNKNOWN", "Unit")));
        Assert.IsType<BadRequestObjectResult>(await staff.Resolve(report.Id, new("FOUND", new string('x', 2001))));
        Assert.Equal("OPEN", report.Status); Assert.Empty(db.Notifications);
    }
    public void Dispose() => db.Dispose();
}
