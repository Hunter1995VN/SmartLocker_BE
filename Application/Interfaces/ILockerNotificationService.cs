namespace Application.Interfaces;

public interface ILockerNotificationService
{
    Task NotifyLockerStatusChangedAsync(Guid stationId, Guid lockerId);
    Task NotifyStationStatusChangedAsync(Guid stationId, string connectivityStatus, DateTimeOffset occurredAt);
    Task NotifyDoorStatusChangedAsync(Guid stationId, Guid lockerId, string lockerCode, string doorState, DateTimeOffset occurredAt);
    Task NotifyLockerUnlockedAsync(Guid stationId, Guid lockerId, string lockerCode);

    /// <summary>
    /// UC-S09: Thông báo vi điều khiển ESP32 đang được khởi động lại từ xa.
    /// </summary>
    Task NotifyDeviceRebootingAsync(Guid stationId, string deviceCode);

    /// <summary>
    /// UC-S07: Broadcast kết quả chẩn đoán tự động Self-Test phần cứng về trạm.
    /// </summary>
    Task NotifySelfTestResultAsync(Guid stationId, string deviceCode, object result);
}
