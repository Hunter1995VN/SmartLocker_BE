using System.Text.Json;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Mqtt;

public sealed record DoorEvent(int SchemaVersion, Guid EventId, string DeviceCode,
    Guid LockerId, string DoorState, DateTimeOffset OccurredAt);

public sealed class DoorEventHandler(
    ISmartLockerDbContext db, 
    ILockerNotificationService notificationService,
    ILogger<DoorEventHandler> logger)
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
        
        // Find locker to update and notify
        var normalizedCode = message.DeviceCode.Replace("-00", "-0");
        var locker = await db.Lockers
            .Include(l => l.Bookings)
            .FirstOrDefaultAsync(l => l.Id == message.LockerId
                && (db.IoTDevices.Any(d => d.Id == l.IoTDeviceId
                    && (d.DeviceCode == message.DeviceCode || d.DeviceCode.Replace("-00", "-0") == normalizedCode) 
                    && d.StationId == l.StationId) || l.IoTDeviceId == null), ct);

        if (locker == null)
        {
            logger.LogWarning("MQTT door ignored: unknown/mismatched locker or device {DeviceCode}.", message.DeviceCode);
            return;
        }

        if (locker.LastDoorEventAt != null && locker.LastDoorEventAt >= timestamp)
        {
            logger.LogWarning("MQTT door ignored: stale event timestamp {Timestamp} <= {LastSeen}", timestamp, locker.LastDoorEventAt);
            return;
        }

        locker.DoorState = message.DoorState;
        locker.LastDoorEventAt = timestamp;
        locker.UpdatedAt = DateTime.UtcNow;

        // Auto transition active booking on door close
        var activeBooking = locker.Bookings
            .OrderByDescending(b => b.CreatedAt)
            .FirstOrDefault(b => b.Status == BookingStatus.CONFIRMED || b.Status == BookingStatus.STORED);

        if (activeBooking != null)
        {
            if (message.DoorState == "CLOSED")
            {
                if (activeBooking.Status == BookingStatus.CONFIRMED && activeBooking.CheckedInAt != null)
                {
                    // Luggage deposited and door shut -> Transition to STORED
                    activeBooking.Status = BookingStatus.STORED;
                    locker.BusinessStatus = LockerBusinessStatus.OCCUPIED;
                    logger.LogInformation("Booking {BookingCode} transitioned to STORED upon door close.", activeBooking.BookingCode);
                }
                else if (activeBooking.Status == BookingStatus.STORED && activeBooking.CheckedOutAt != null)
                {
                    // Luggage retrieved and door shut -> Transition to COMPLETED & free locker
                    activeBooking.Status = BookingStatus.COMPLETED;
                    locker.BusinessStatus = LockerBusinessStatus.AVAILABLE;
                    logger.LogInformation("Booking {BookingCode} transitioned to COMPLETED upon door close.", activeBooking.BookingCode);
                }
            }
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("MQTT door updated: locker {LockerCode} ({LockerId}) -> {DoorState}; event {EventId}",
            locker.LockerCode, message.LockerId, message.DoorState, message.EventId);

        // Notify Kiosk and Admin UI via SignalR
        try
        {
            await notificationService.NotifyDoorStatusChangedAsync(
                locker.StationId,
                locker.Id,
                locker.LockerCode,
                message.DoorState,
                message.OccurredAt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to broadcast door event to SignalR clients.");
        }
    }
}
