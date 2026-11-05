using System.Security.Cryptography;
using Application.DTOs.Booking;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Controllers;

[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("kiosk")]
[ApiController, Route("api/kiosk/walk-in")]
public class WalkInController(ISmartLockerDbContext db, IBookingService bookings) : ControllerBase
{
    public record CreateRequest(Guid StationId, string Size, int Hours, string Phone);
    public record SessionRequest(string Secret);
    public record CancelRequest(string Secret, string? Reason);

    [HttpPost]
    public async Task<IActionResult> Create(CreateRequest request)
    {
        if (request.StationId == Guid.Empty || request.Size is not ("S" or "M" or "L") || request.Hours is < 1 or > 72
            || !SupportInput.IsContact(request.Phone) || request.Phone.Contains('@'))
            return BadRequest(new { message = "Chọn kích cỡ, 1–72 giờ và số điện thoại liên hệ hợp lệ." });
        if (!await db.Stations.AnyAsync(s => s.Id == request.StationId && s.Status == StationStatus.ACTIVE))
            return BadRequest(new { message = "Trạm không tồn tại hoặc hiện không hoạt động." });
        var size = Enum.Parse<LockerSize>(request.Size);
        if (!await LockerAvailability.ReadyLockers(db, request.StationId, DateTime.UtcNow).AnyAsync(l => l.Size == size))
            return BadRequest(new { message = "Chưa có ô tủ phù hợp với thiết bị đang kết nối. Vui lòng liên hệ nhân viên trước khi thanh toán." });
        // A guest is a distinct principal. Never attach an unverified phone to an existing customer.
        var guestId = Guid.NewGuid();
        var guest = new User { Id = guestId, FullName = "Khách tại kiosk", Email = guestId.ToString("N") + "@guest.invalid",
            Phone = "G" + guestId.ToString("N")[..18], PasswordHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))),
            Role = UserRole.TRAVELER, Status = UserStatus.ACTIVE };
        db.Users.Add(guest); await db.SaveChangesAsync();
        var start = DateTime.UtcNow;
        var result = await bookings.CreateBookingAsync(guestId, new CreateBookingRequest {
            StationId = request.StationId, Size = request.Size, StartAt = start, EndAt = start.AddHours(request.Hours),
             });
        if (!result.Success || result.Data == null)
        {
            // Compensate only the newly-created guest if the booking transaction rolled back.
            if (db.Database.IsRelational())
                await db.Users.Where(u => u.Id == guestId && !db.Bookings.Any(b => b.UserId == guestId)).ExecuteDeleteAsync();
            return BadRequest(new { message = result.Message });
        }
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new WalkInSession { Id = Guid.NewGuid(), BookingId = result.Data.BookingId,
            SecretHash = OnlineTicket.HashPin(secret), ContactPhone = request.Phone, ExpiresAt = start.AddMinutes(45) };
        db.WalkInSessions.Add(session); await db.SaveChangesAsync();
        Response.Headers.CacheControl = "no-store";
        return Ok(new { session.Id, secret, session.ExpiresAt, payment = result.Data });
    }

    [HttpPost("{id:guid}/status")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("kiosk-read")]
    public async Task<IActionResult> Status(Guid id, SessionRequest request)
    {
        if (string.IsNullOrEmpty(request.Secret) || request.Secret.Length != 64) return NotFound();
        var hash = OnlineTicket.HashPin(request.Secret);
        var session = await db.WalkInSessions.Include(x => x.Booking)
            .FirstOrDefaultAsync(x => x.Id == id && x.SecretHash == hash && x.ExpiresAt > DateTime.UtcNow);
        if (session == null) return NotFound();
        // Provider reconciliation is delegated to the existing verified payment service.
        var result = await bookings.GetBookingByIdAsync(session.Booking.UserId, session.BookingId);
        if (!result.Success || result.Data == null) return BadRequest(new { message = result.Message });
        Response.Headers.CacheControl = "no-store";
        return Ok(new { result.Data.Id, result.Data.BookingCode, result.Data.Status, result.Data.AccessCode, result.Data.QrPayload });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancelRequest request)
    {
        if (string.IsNullOrEmpty(request.Secret) || request.Secret.Length != 64 || request.Reason?.Length > 500) return BadRequest();
        var hash = OnlineTicket.HashPin(request.Secret);
        var session = await db.WalkInSessions.Include(x => x.Booking)
            .FirstOrDefaultAsync(x => x.Id == id && x.SecretHash == hash && x.ExpiresAt > DateTime.UtcNow);
        if (session == null) return NotFound();
        var current = await bookings.GetBookingByIdAsync(session.Booking.UserId, session.BookingId);
        if (!current.Success || current.Data == null) return BadRequest(new { message = current.Message });
        if (current.Data.Status != "PENDING_PAYMENT" || await db.Payments.AnyAsync(p => p.BookingId == session.BookingId && p.Status == PaymentStatus.PAID))
            return Conflict(new { message = "Chỉ hủy tại đây khi đơn chưa thanh toán. Vui lòng liên hệ nhân viên nếu đã chuyển tiền." });
        var result = await bookings.CancelBookingAsync(session.Booking.UserId, session.BookingId,
            new CancelBookingRequest { Reason = request.Reason?.Trim() ?? "Khách hủy đơn Walk-in chưa thanh toán tại Kiosk" });
        if (!result.Success) return BadRequest(new { message = result.Message });
        session.ExpiresAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { status = result.Data?.Status });
    }
}
