namespace Application.Interfaces;

public interface ILockerNotificationService
{
    Task NotifyStationStatusChangedAsync(Guid stationId, string connectivityStatus, DateTimeOffset occurredAt);
    Task NotifyDoorStatusChangedAsync(Guid stationId, Guid lockerId, string lockerCode, string doorState, DateTimeOffset occurredAt);
    Task NotifyLockerUnlockedAsync(Guid stationId, Guid lockerId, string lockerCode);
}
