using System.Text.Json;
using Application.Interfaces;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
namespace Infrastructure.Mqtt;

public sealed class HeartbeatEventHandler(ISmartLockerDbContext db, ILockerNotificationService notifications)
{
    public async Task HandleAsync(string topic, string payload, bool retained, CancellationToken ct)
    {
        if (retained || payload.Length > 4096) return;
        string code; long uptime; short rssi;
        var doors = new Dictionary<LockerSize, string>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            code = root.GetProperty("deviceCode").GetString() ?? "";
            if (code.Length is < 1 or > 100 || code.IndexOfAny(['/', '+', '#']) >= 0
                || topic != $"smartlocker/devices/{code}/heartbeat" || root.GetProperty("status").GetString() != "ONLINE"
                || !root.GetProperty("uptime").TryGetInt64(out uptime) || uptime < 0
                || !root.GetProperty("rssi").TryGetInt16(out rssi) || rssi is < -127 or > 0) return;
            foreach (var size in Enum.GetValues<LockerSize>())
                if (root.TryGetProperty("door" + size, out var state) && state.ValueKind == JsonValueKind.String
                    && state.GetString() is "OPEN" or "CLOSED") doors[size] = state.GetString()!;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return; }
        var device = await db.IoTDevices.SingleOrDefaultAsync(x => x.DeviceCode == code, ct);
        if (device == null) return;
        var now = DateTime.UtcNow;
        var changed = device.ConnectivityStatus != "ONLINE" || device.LastSeenAt == null || device.LastSeenAt < now.AddSeconds(-60);
        device.ConnectivityStatus = "ONLINE";
        device.LastSeenAt = now;
        device.UpdatedAt = now;
        device.UptimeSeconds = uptime;
        device.WifiRssi = rssi;
        // Recover the current empty-door view after the API missed transient events.
        // Snapshots cannot participate in a deposit/pickup: only update idle compartments,
        // and never advance the real transition timestamp or any booking status.
        var mapped = await db.Lockers.Where(l => l.IoTDeviceId == device.Id && l.StationId == device.StationId).ToListAsync(ct);
        var activeLockerIds = await db.Bookings.Where(b => b.StationId == device.StationId && b.LockerId != null &&
            (b.Status == BookingStatus.PENDING_PAYMENT || b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED))
            .Select(b => b.LockerId!.Value).ToListAsync(ct);
        var snapshotsChanged = false;
        foreach (var group in mapped.GroupBy(l => l.Size))
        {
            // This firmware identifies one physical bay per size; never choose among duplicates.
            if (group.Count() != 1 || !doors.TryGetValue(group.Key, out var state)) continue;
            var locker = group.Single();
            if (state != "CLOSED" || activeLockerIds.Contains(locker.Id) || locker.BusinessStatus != LockerBusinessStatus.AVAILABLE || locker.DoorState == state) continue;
            locker.DoorState = state;
            locker.UpdatedAt = now;
            snapshotsChanged = true;
        }
        await db.SaveChangesAsync(ct);
        if (changed || snapshotsChanged)
            await notifications.NotifyStationStatusChangedAsync(device.StationId, "ONLINE", new DateTimeOffset(now));
    }
}
