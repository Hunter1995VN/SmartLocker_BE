namespace Infrastructure.Services;
using Application.DTOs.Kiosk;
using Application.Interfaces;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

// Aggregates real orders without fabricating a physical compartment assignment.
public static class StationReservationCounts
{
    public static async Task<List<KioskSizeReservationDto>> ReadAsync(ISmartLockerDbContext db, Guid stationId)
    {
        var now = DateTime.UtcNow;
        var active = await db.Bookings.AsNoTracking().Where(b => b.StationId == stationId &&
            (b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED ||
             (b.Status == BookingStatus.PENDING_PAYMENT && b.PaymentExpiresAt > now)))
            .Select(b => new { b.Size, b.Status, b.LockerId }).ToListAsync();
        return Enum.GetValues<LockerSize>().Select(size => new KioskSizeReservationDto {
            Size = size.ToString(),
            PendingPayment = active.Count(b => b.Size == size && b.Status == BookingStatus.PENDING_PAYMENT),
            AwaitingDeposit = active.Count(b => b.Size == size && b.Status == BookingStatus.CONFIRMED),
            UnassignedReservations = active.Count(b => b.Size == size && b.Status == BookingStatus.CONFIRMED && b.LockerId == null),
            Stored = active.Count(b => b.Size == size && b.Status == BookingStatus.STORED)
        }).ToList();
    }
}
