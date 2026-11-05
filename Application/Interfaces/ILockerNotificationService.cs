namespace Application.Interfaces;

public interface ILockerNotificationService
{
    Task NotifyLockerStatusChangedAsync(Guid stationId, Guid lockerId);
    Task NotifyStationStatusChangedAsync(Guid stationId, string connectivityStatus, DateTimeOffset occurredAt);
    Task NotifyDoorStatusChangedAsync(Guid stationId, Guid lockerId, string lockerCode, string doorState, DateTimeOffset occurredAt);
    Task NotifyLockerUnlockedAsync(Guid stationId, Guid lockerId, string lockerCode);
}
