namespace Application.Interfaces;

public interface ILockerNotificationService
{
    Task NotifyDoorStatusChangedAsync(Guid stationId, Guid lockerId, string lockerCode, string doorState, DateTimeOffset occurredAt);
    Task NotifyLockerUnlockedAsync(Guid stationId, Guid lockerId, string lockerCode);
}
