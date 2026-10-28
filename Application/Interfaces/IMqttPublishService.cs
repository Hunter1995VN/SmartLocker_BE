namespace Application.Interfaces;

public interface IMqttPublishService
{
    /// <summary>
    /// Phát lệnh mở khóa (UNLOCK) xuống thiết bị ESP32 qua MQTT.
    /// Topic: smartlocker/devices/{deviceCode}/cmd
    /// </summary>
    Task<bool> PublishUnlockCommandAsync(string deviceCode, Guid lockerId, string lockerCode, string? requestId = null, int durationMs = 500);

    /// <summary>
    /// Phát thông điệp thông báo chung xuống trạm (nếu cần).
    /// </summary>
    Task<bool> PublishMessageAsync(string topic, string payload, bool retain = false);
}
