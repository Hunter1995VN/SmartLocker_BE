using System.Text.Json;
using Application.DTOs.Admin;
using Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Mqtt;

public sealed class DiagnosticsEventHandler(
    ISmartLockerDbContext db,
    ILockerNotificationService notifications,
    ILogger<DiagnosticsEventHandler> logger)
{
    public async Task HandleAsync(string topic, string payload, bool retained, CancellationToken ct)
    {
        if (retained || string.IsNullOrWhiteSpace(payload) || payload.Length > 4096)
            return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var deviceCode = root.TryGetProperty("deviceCode", out var dc) ? dc.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(deviceCode))
                return;

            var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "HEALTHY" : "HEALTHY";
            var durationMs = root.TryGetProperty("durationMs", out var dur) ? dur.GetInt32() : 0;
            var wifiRssi = root.TryGetProperty("wifiRssi", out var rssi) ? rssi.GetInt16() : (short)-50;
            var freeHeapBytes = root.TryGetProperty("freeHeapBytes", out var heap) ? heap.GetInt64() : 0;
            var uptimeSeconds = root.TryGetProperty("uptimeSeconds", out var upt) ? upt.GetInt64() : 0;
            var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : DateTime.UtcNow.ToString("O");

            var relays = new Dictionary<string, string>();
            if (root.TryGetProperty("relays", out var relaysObj) && relaysObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in relaysObj.EnumerateObject())
                {
                    relays[prop.Name] = prop.Value.GetString() ?? "OK";
                }
            }

            var doors = new Dictionary<string, string>();
            if (root.TryGetProperty("doors", out var doorsObj) && doorsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doorsObj.EnumerateObject())
                {
                    doors[prop.Name] = prop.Value.GetString() ?? "CLOSED";
                }
            }

            var device = await db.IoTDevices.FirstOrDefaultAsync(d => d.DeviceCode == deviceCode, ct);
            var stationId = Guid.Empty;

            if (device != null)
            {
                stationId = device.StationId;
                device.ConnectivityStatus = "ONLINE";
                device.LastSeenAt = DateTime.UtcNow;
                device.WifiRssi = wifiRssi;
                device.UptimeSeconds = uptimeSeconds;
                device.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                var firstStation = await db.Stations.FirstOrDefaultAsync(ct);
                if (firstStation != null)
                    stationId = firstStation.Id;
            }

            logger.LogInformation("Diagnostics result processed for device {DeviceCode}: Status={Status}, Duration={DurationMs}ms, Station={StationId}",
                deviceCode, status, durationMs, stationId);

            var resultDto = new DiagnosticsResultDto
            {
                SchemaVersion = 1,
                DeviceCode = deviceCode,
                Status = status,
                DurationMs = durationMs,
                Relays = relays,
                Doors = doors,
                WifiRssi = wifiRssi,
                FreeHeapBytes = freeHeapBytes,
                UptimeSeconds = uptimeSeconds,
                Timestamp = timestamp
            };

            if (stationId != Guid.Empty)
            {
                await notifications.NotifySelfTestResultAsync(stationId, deviceCode, resultDto);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse or process diagnostics payload from topic {Topic}", topic);
        }
    }
}
