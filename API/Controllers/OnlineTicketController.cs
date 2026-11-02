using System.Security.Claims;
using System.Security.Cryptography;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[ApiController, Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/bookings/{bookingId:guid}/ticket")]
public class OnlineTicketController(ISmartLockerDbContext db, IAccessPinProtector protector,
    IPaymentGatewayService gateway) : ControllerBase
{
    public record UpgradeRequest(bool ConfirmReplaceLegacyCredential);

    // Explicit owner action for legacy credentials; a ticket read never changes the PIN.
    [HttpPost("upgrade-pin")]
    public Task<IActionResult> Upgrade(Guid bookingId, UpgradeRequest request) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        if (!request.ConfirmReplaceLegacyCredential)
            return (IActionResult)BadRequest(new { message = "Cần xác nhận thay vé cũ bằng PIN 8 số." });
        var claim = User.FindFirstValue("uid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var owner)) return Unauthorized();
        if (db.Database.IsRelational() && db is DbContext tracked) tracked.ChangeTracker.Clear();
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable) : null;
        var booking = await db.Bookings.Include(x => x.AccessCredential).Include(x => x.Payments)
            .FirstOrDefaultAsync(x => x.Id == bookingId && x.UserId == owner);
        if (booking == null) return NotFound();
        if (booking.Status != BookingStatus.CONFIRMED || booking.CheckedInAt != null
            || booking.AccessCredential is not { IsActive: true, RevokedAt: null } credential)
            return BadRequest(new { message = "Chỉ thay vé cho đơn đã thanh toán, chưa bắt đầu mở tủ." });
        if (OnlineTicket.GetDisplayPin(credential, protector) != null)
            return NoContent(); // A repeated request must not invalidate the newly issued ticket.
        var legacyPin = credential.OfflinePayload;
        if (credential.ProtectedAccessCode != null || legacyPin is not { Length: 6 }
            || !legacyPin.All(char.IsAsciiDigit) || OnlineTicket.HashPin(legacyPin) != credential.AccessCodeHash)
            return BadRequest(new { message = "Không xác minh được vé cũ. Vui lòng liên hệ nhân viên." });
        var payment = booking.Payments.Where(x => x.Kind == PaymentKind.BASE && x.Status == PaymentStatus.PAID)
            .OrderByDescending(x => x.PaidAt).FirstOrDefault();
        if (payment?.PaidAt == null || payment.PaidAt < DateTime.UtcNow.AddMinutes(-30)
            || !long.TryParse(payment.OrderCode, out var orderCode)
            || !string.Equals(await gateway.GetPaymentStatusAsync(orderCode), "PAID", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Đơn chưa được cổng thanh toán xác nhận hoặc đã hết hạn nhận tủ." });
        var pin = RandomNumberGenerator.GetInt32(0, 100000000).ToString("D8");
        credential.AccessCodeHash = OnlineTicket.HashPin(pin);
        credential.ProtectedAccessCode = protector.Protect(booking.Id, pin);
        credential.OfflinePayload = null;
        credential.OfflineNonce = Guid.NewGuid().ToString("N");
        credential.OfflineExpiresAt = null;
        credential.AuthFailedAttempts = 0;
        credential.AuthLockUntil = null;
        OnlineTicket.Rotate(credential, booking.StationId, DateTime.UtcNow);
        db.AuditLogs.Add(new AuditLog { UserId = owner, Action = "UPGRADE_ACCESS_PIN", TargetEntity = "Booking",
            TargetId = booking.Id.ToString(), Details = "Owner explicitly replaced a verified legacy six-digit credential; payment and booking state unchanged." });
        await db.SaveChangesAsync();
        if (transaction != null) await transaction.CommitAsync();
        return NoContent();
    });
}
