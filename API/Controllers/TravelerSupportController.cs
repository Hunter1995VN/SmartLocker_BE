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
[Route("api/bookings/{bookingId:guid}/support")]
public class TravelerSupportController(ISmartLockerDbContext db) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirstValue("uid") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
    private Task<Booking?> Owned(Guid id) => db.Bookings.Include(b => b.Locker).FirstOrDefaultAsync(b => b.Id == id && b.UserId == UserId);
    public record DelegateRequest(string Contact, int ValidMinutes = 60);
    public record ReviewRequest(int Stars, string? Comment, string[]? Criteria);
    public record LostRequest(string Description, string? Photo);

    [HttpGet]
    public async Task<IActionResult> Get(Guid bookingId)
    {
        if (await Owned(bookingId) == null) return NotFound();
        return Ok(new {
            delegations = await db.PickupDelegations.Where(x => x.BookingId == bookingId).Select(x => new { x.Id, x.Contact, x.ExpiresAt, x.UsedAt, x.RevokedAt }).ToListAsync(),
            review = await db.StationReviews.Where(x => x.BookingId == bookingId).Select(x => new { x.Stars, x.Comment, x.Criteria }).FirstOrDefaultAsync(),
            lostReports = await db.LostItemReports.Where(x => x.BookingId == bookingId).Select(x => new { x.Id, x.Description, x.Status, x.StaffResponse }).ToListAsync()
        });
    }

    [HttpPost("delegations")]
    public async Task<IActionResult> Delegate(Guid bookingId, DelegateRequest request)
    {
        var booking = await Owned(bookingId);
        if (booking == null) return NotFound();
        if (booking.Status != BookingStatus.STORED || booking.CheckedOutAt != null
            || request.ValidMinutes is < 1 or > 1440 || string.IsNullOrWhiteSpace(request.Contact) || request.Contact.Length > 255)
            return BadRequest(new { message = "Chỉ ủy quyền đơn đang lưu trữ. Cần thông tin người nhận và thời hạn 1–1440 phút." });
        var expiry = DateTime.UtcNow.AddMinutes(request.ValidMinutes);
        if (expiry > booking.EndAt) expiry = booking.EndAt;
        if (expiry <= DateTime.UtcNow) return BadRequest(new { message = "Gia hạn đơn trước khi tạo mã ủy quyền." });
        var old = await db.PickupDelegations.Where(x => x.BookingId == bookingId && x.RevokedAt == null && x.UsedAt == null).ToListAsync();
        foreach (var item in old) item.RevokedAt = DateTime.UtcNow;
        var pin = RandomNumberGenerator.GetInt32(0, 100000000).ToString("D8");
        var grant = new PickupDelegation { Id = Guid.NewGuid(), BookingId = bookingId,
            Contact = request.Contact.Trim(), PinHash = OnlineTicket.HashPin(pin), ExpiresAt = expiry };
        db.PickupDelegations.Add(grant);
        db.AuditLogs.Add(new AuditLog { UserId = UserId, Action = "CREATE_PICKUP_DELEGATION", TargetEntity = "Booking", TargetId = bookingId.ToString(), Details = "Delegate " + grant.Id });
        await db.SaveChangesAsync();
        Response.Headers.CacheControl = "no-store";
        return Ok(new { grant.Id, pin, grant.ExpiresAt });
    }

    [HttpDelete("delegations/{id:guid}")]
    public async Task<IActionResult> Revoke(Guid bookingId, Guid id)
    {
        if (await Owned(bookingId) == null) return NotFound();
        var grant = await db.PickupDelegations.FirstOrDefaultAsync(x => x.Id == id && x.BookingId == bookingId);
        if (grant == null) return NotFound();
        if (grant.UsedAt != null) return Conflict(new { message = "Mã đã được sử dụng." });
        grant.RevokedAt = DateTime.UtcNow; await db.SaveChangesAsync(); return NoContent();
    }

}
