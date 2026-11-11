using System.Security.Claims;
using API.Controllers;
using API.Controllers.Admin;
using Application.Interfaces;
using Moq;
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
    private readonly Mock<IPickupDelegationNotifier> notifier = new();
    private static ControllerContext Context(Guid user) => new() { HttpContext = new DefaultHttpContext {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", user.ToString()), new Claim(ClaimTypes.Role, "STAFF")], "Test")) } };
    public SupportTests()
    {
        booking = new Booking { Id = Guid.NewGuid(), UserId = owner, StationId = Guid.NewGuid(), Status = BookingStatus.COMPLETED,
            EndAt = DateTime.UtcNow.AddHours(2), CheckedOutAt = DateTime.UtcNow,
            Locker = new Locker { Id = Guid.NewGuid(), BusinessStatus = LockerBusinessStatus.AVAILABLE } };
        booking.LockerId = booking.Locker.Id; db.Add(booking); db.SaveChanges();
        notifier.Setup(n => n.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DelegationDelivery("EMAIL", true, null));
        controller = new TravelerSupportController(db, notifier.Object) { ControllerContext = Context(owner) };
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
    [Theory]
    [InlineData(BookingStatus.PENDING_PAYMENT)]
    [InlineData(BookingStatus.CONFIRMED)]
    [InlineData(BookingStatus.STORED)]
    public async Task LostReportNeverSeizesLockerWithAnotherActiveBooking(BookingStatus nextStatus)
    {
        db.Bookings.Add(new Booking { Id = Guid.NewGuid(), LockerId = booking.LockerId,
            StationId = booking.StationId, UserId = Guid.NewGuid(), Status = nextStatus });
        await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await controller.ReportLost(booking.Id, new("Jacket", null)));
        Assert.Equal(LockerBusinessStatus.AVAILABLE, booking.Locker!.BusinessStatus);
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
        Assert.IsType<BadRequestObjectResult>(await staff.Approve(booking.Id, new("Missing evidence must be rejected")));
        var folder = Path.Combine(AppContext.BaseDirectory, "App_Data", "manual-confirm");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, booking.ManualConfirmPhotoUrl);
        // Storage fixture for this controller unit test; not a camera acceptance test.
        await File.WriteAllBytesAsync(path, [0xff, 0xd8, 0xff, 0xd9]);
        try { Assert.IsType<OkObjectResult>(await staff.Approve(booking.Id, new("Verified empty locker and door"))); }
        finally { File.Delete(path); }
        Assert.Equal(BookingStatus.COMPLETED_MANUAL, booking.Status);
        Assert.Equal(LockerBusinessStatus.MAINTENANCE, booking.Locker!.BusinessStatus);
        Assert.Single(db.AuditLogs);
    }
    [Fact] public async Task ManuallyCompletedBookingCanReviewAndReportLostItems()
    {
        booking.Status = BookingStatus.COMPLETED_MANUAL;
        await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await controller.Review(booking.Id, new(4, "", ["FAST"])));
        Assert.IsType<OkObjectResult>(await controller.ReportLost(booking.Id, new("Jacket", null)));
    }
    [Fact] public async Task StaffLostResponseQueuesOwnedNotificationAndResolvesPriorityIncidentOnce()
    {
        Assert.IsType<OkObjectResult>(await controller.ReportLost(booking.Id, new("Jacket", null)));
        var report = Assert.Single(db.LostItemReports);
        Assert.Equal("HIGH", Assert.Single(db.SecurityIncidents).Severity);
        var staff = new TravelerSupportAdminController(db) { ControllerContext = Context(owner) };
        Assert.IsType<OkObjectResult>(await staff.Resolve(report.Id, new("NOT_FOUND", "Đã kiểm tra tủ")));
        Assert.Equal(IncidentStatus.RESOLVED, Assert.Single(db.SecurityIncidents).Status);
        var notification = Assert.Single(db.Notifications);
        Assert.Equal(owner, notification.UserId);
        Assert.Equal("PENDING", notification.Status);
        Assert.IsType<OkObjectResult>(await staff.Resolve(report.Id, new("NOT_FOUND", "Đã kiểm tra tủ")));
        Assert.Single(db.Notifications);
        controller.ControllerContext = Context(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(await controller.ReadNotification(booking.Id, notification.Id));
        Assert.Null(notification.ReadAt);
        controller.ControllerContext = Context(owner);
        Assert.IsType<NoContentResult>(await controller.ReadNotification(booking.Id, notification.Id));
        Assert.Equal("READ", notification.Status);
        Assert.NotNull(notification.ReadAt);
    }

    [Theory]
    [InlineData("data:image/jpeg;base64,not-base64")]
    [InlineData("data:image/jpeg;base64,/9j/2Q==")]
    [InlineData("data:image/png;base64,/9j/2Q==")]
    public async Task InvalidLostPhotoDoesNotCreateReportOrIncident(string photo)
    {
        Assert.IsType<BadRequestObjectResult>(await controller.ReportLost(booking.Id, new("Jacket", photo)));
        Assert.Empty(db.LostItemReports); Assert.Empty(db.SecurityIncidents);
    }

    [Theory] [InlineData("plain text")] [InlineData("++++12345678")]
    public void InvalidPhoneOrContactIsRejected(string contact)
    {
        Assert.False(Infrastructure.Services.SupportInput.IsContact(contact));
    }
    private void MakeStored() { booking.Status = BookingStatus.STORED; booking.CheckedOutAt = null; db.SaveChanges(); }
    private static System.Text.Json.JsonElement Json(object? value) => System.Text.Json.JsonSerializer.SerializeToElement(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    [Fact] public async Task DelegationToEmailSendsPinOnceAfterSave()
    {
        MakeStored();
        var ok = Assert.IsType<OkObjectResult>(await controller.Delegate(booking.Id, new("friend@example.com", 60)));
        var body = Json(ok.Value);
        var pin = body.GetProperty("pin").GetString()!;
        Assert.Matches("^[0-9]{8}$", pin);
        Assert.Equal("EMAIL", body.GetProperty("delivery").GetProperty("channel").GetString());
        Assert.True(body.GetProperty("delivery").GetProperty("sent").GetBoolean());
        notifier.Verify(n => n.SendAsync("friend@example.com", pin, It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(db.PickupDelegations.Where(x => x.RevokedAt == null && x.UsedAt == null));
    }

    [Fact] public async Task PhoneDelegationIsReportedAsManualAndStillCreated()
    {
        MakeStored();
        notifier.Setup(n => n.SendAsync("0901234567", It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DelegationDelivery("MANUAL", false, "NO_SMS_PROVIDER"));
        var ok = Assert.IsType<OkObjectResult>(await controller.Delegate(booking.Id, new("0901234567", 30)));
        var delivery = Json(ok.Value).GetProperty("delivery");
        Assert.Equal("MANUAL", delivery.GetProperty("channel").GetString());
        Assert.False(delivery.GetProperty("sent").GetBoolean());
        Assert.Equal("NO_SMS_PROVIDER", delivery.GetProperty("reason").GetString());
        Assert.Single(db.PickupDelegations);
    }

    [Fact] public async Task NotifierFailureNeverCancelsDelegationAndPinStaysAvailable()
    {
        MakeStored();
        notifier.Setup(n => n.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        var ok = Assert.IsType<OkObjectResult>(await controller.Delegate(booking.Id, new("friend@example.com", 30)));
        var body = Json(ok.Value);
        Assert.Matches("^[0-9]{8}$", body.GetProperty("pin").GetString()!);
        Assert.False(body.GetProperty("delivery").GetProperty("sent").GetBoolean());
        Assert.Equal("SEND_FAILED", body.GetProperty("delivery").GetProperty("reason").GetString());
        Assert.Single(db.PickupDelegations);
    }

    [Fact] public async Task RejectedDelegationNeverNotifiesAnyone()
    {
        Assert.IsType<BadRequestObjectResult>(await controller.Delegate(booking.Id, new("friend@example.com", 60))); // đơn đã hoàn tất
        MakeStored();
        Assert.IsType<BadRequestObjectResult>(await controller.Delegate(booking.Id, new("++++12345678", 60)));
        Assert.IsType<BadRequestObjectResult>(await controller.Delegate(booking.Id, new("friend@example.com", 0)));
        controller.ControllerContext = Context(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(await controller.Delegate(booking.Id, new("friend@example.com", 60)));
        notifier.Verify(n => n.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(db.PickupDelegations);
    }

    [Fact] public async Task NotifierRoutesByContactTypeAndNeverThrows()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Infrastructure.Services.EmailPickupDelegationNotifier>.Instance;
        var unconfigured = new Mock<Microsoft.Extensions.Options.IOptionsSnapshot<Infrastructure.Services.SmtpSettings>>();
        unconfigured.SetupGet(o => o.Value).Returns(new Infrastructure.Services.SmtpSettings { Username = "", Password = "" });
        var sender = new Infrastructure.Services.EmailPickupDelegationNotifier(unconfigured.Object, logger);
        var phone = await sender.SendAsync("0901234567", "01234567", DateTime.UtcNow.AddHours(1), "Trạm A", "BK1");
        Assert.Equal(new DelegationDelivery("MANUAL", false, "NO_SMS_PROVIDER"), phone);
        var mail = await sender.SendAsync("friend@example.com", "01234567", DateTime.UtcNow.AddHours(1), "Trạm A", "BK1");
        Assert.Equal(new DelegationDelivery("EMAIL", false, "SMTP_NOT_CONFIGURED"), mail);
    }

    [Theory] [InlineData("+84 901 234 567")] [InlineData("(028) 3825-1234")] [InlineData("0901234567")] [InlineData("a@b.co")]
    public void ValidContactsAreAccepted(string contact) => Assert.True(Infrastructure.Services.SupportInput.IsContact(contact));

    [Theory] [InlineData("+84+901234567")] [InlineData("0901-abc-4567")] [InlineData("1234567")] [InlineData("1234567890123456")] [InlineData("a@")]
    public void MalformedContactsAreRejected(string contact) => Assert.False(Infrastructure.Services.SupportInput.IsContact(contact));

    public void Dispose() => db.Dispose();
}
