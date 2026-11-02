namespace Infrastructure.Services;

using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;

/// <summary>Capacity that can actually be assigned by the online kiosk.</summary>
public static class LockerAvailability
{
    public static IQueryable<Locker> ReadyLockers(ISmartLockerDbContext db, Guid? stationId, DateTime now) =>
        db.Lockers.Where(l => (stationId == null || l.StationId == stationId)
            && l.BusinessStatus == LockerBusinessStatus.AVAILABLE
            && l.HealthStatus == LockerHealthStatus.HEALTHY && l.DoorState == "CLOSED"
            && db.IoTDevices.Any(d => d.Id == l.IoTDeviceId && d.StationId == l.StationId
                && d.ConnectivityStatus == "ONLINE" && d.LastSeenAt >= now.AddSeconds(-60))
            && !db.Bookings.Any(b => b.LockerId == l.Id
                && (b.Status == BookingStatus.PENDING_PAYMENT || b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED)));

    public static async Task<int> CountAsync(ISmartLockerDbContext db, Guid stationId, LockerSize size,
        DateTime from, DateTime to, DateTime now)
    {
        var ready = await ReadyLockers(db, stationId, now).CountAsync(l => l.Size == size);
        // Assigned reservations already exclude their locker above; subtract only size reservations.
        var reservations = await db.Bookings.CountAsync(b => b.StationId == stationId && b.Size == size
            && b.LockerId == null && b.StartAt < to && b.EndAt > from
            && (b.Status == BookingStatus.CONFIRMED
                || (b.Status == BookingStatus.PENDING_PAYMENT && b.PaymentExpiresAt > now)));
        return Math.Max(0, ready - reservations);
    }
}
