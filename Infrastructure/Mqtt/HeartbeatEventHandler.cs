using System.Text.Json;
using Application.Interfaces;
using Microsoft.EntityFrameworkCore;
namespace Infrastructure.Mqtt;

public sealed class HeartbeatEventHandler(ISmartLockerDbContext db, ILockerNotificationService notifications)
{
    public async Task HandleAsync(string topic, string payload, bool retained, CancellationToken ct)
    {
        if (retained || payload.Length > 4096) return;
        string code; long uptime; short rssi;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            code = root.GetProperty("deviceCode").GetString() ?? "";
            if (code.Length is < 1 or > 100 || code.IndexOfAny(['/', '+', '#']) >= 0
                || topic != $"smartlocker/devices/{code}/heartbeat" || root.GetProperty("status").GetString() != "ONLINE"
                || !root.GetProperty("uptime").TryGetInt64(out uptime) || uptime < 0
                || !root.GetProperty("rssi").TryGetInt16(out rssi) || rssi is < -127 or > 0) return;
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
        // doorS/M/L are snapshots, never evidence authorizing booking completion.
        await db.SaveChangesAsync(ct);
        if (changed) await notifications.NotifyStationStatusChangedAsync(device.StationId, "ONLINE", new DateTimeOffset(now));
    }
}
