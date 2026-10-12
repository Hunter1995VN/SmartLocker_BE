using System.Text.Json;
using Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Mqtt;

public sealed record DoorEvent(int SchemaVersion, Guid EventId, string DeviceCode,
    Guid LockerId, string DoorState, DateTimeOffset OccurredAt);

public sealed class DoorEventHandler(ISmartLockerDbContext db, ILogger<DoorEventHandler> logger)
{
    public static DoorEvent Parse(string topic, string payload, DateTimeOffset now)
    {
        if (payload.Length > 4096) throw new ArgumentException("Payload exceeds 4096 characters.");
        var message = JsonSerializer.Deserialize<DoorEvent>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("Missing door event.");
        if (message.SchemaVersion != 1 || message.EventId == Guid.Empty || message.LockerId == Guid.Empty
            || string.IsNullOrWhiteSpace(message.DeviceCode) || message.DeviceCode.Length > 100
            || message.DeviceCode.IndexOfAny(['/', '+', '#']) >= 0
            || topic != $"smartlocker/devices/{message.DeviceCode}/door"
            || message.DoorState is not ("OPEN" or "CLOSED")
            || message.OccurredAt == default || message.OccurredAt > now.AddMinutes(2)
            || message.OccurredAt < now.AddHours(-24))
            throw new ArgumentException("Invalid door event identity, topic, state or timestamp.");
        return message;
    }

    public async Task HandleAsync(string topic, string payload, bool retained, CancellationToken ct)
    {
        // Door events are transient: do not replay retained snapshots on reconnect.
        if (retained) { logger.LogWarning("Ignored retained MQTT door event."); return; }
        DoorEvent message;
        try { message = Parse(topic, payload, DateTimeOffset.UtcNow); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            logger.LogWarning("Ignored invalid MQTT door event ({ErrorType}).", ex.GetType().Name);
            return;
        }

        var timestamp = message.OccurredAt.UtcDateTime;
        // One conditional UPDATE makes mapping and timestamp checks atomic. Duplicate
        // or older events cannot overwrite a newer state, even with concurrent clients.
        var updated = await db.Lockers
            .Where(l => l.Id == message.LockerId
                && db.IoTDevices.Any(d => d.Id == l.IoTDeviceId
                    && d.DeviceCode == message.DeviceCode && d.StationId == l.StationId)
                && (l.LastDoorEventAt == null || l.LastDoorEventAt < timestamp))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(l => l.DoorState, message.DoorState)
                .SetProperty(l => l.LastDoorEventAt, timestamp)
                .SetProperty(l => l.UpdatedAt, DateTime.UtcNow), ct);
        if (updated == 1)
            logger.LogInformation("MQTT door updated: locker {LockerId} -> {DoorState}; event {EventId}",
                message.LockerId, message.DoorState, message.EventId);
        else
            logger.LogWarning("MQTT door ignored: unknown/mismatched locker or stale event {EventId}.", message.EventId);
    }
}
