namespace Application.Interfaces;

public interface IMqttPublishService
{
    /// <summary>
    /// Phát lệnh mở khóa (UNLOCK) xuống thiết bị ESP32 qua MQTT.
    /// Topic: smartlocker/devices/{deviceCode}/cmd
    /// </summary>
    Task<bool> PublishUnlockCommandAsync(string deviceCode, Guid lockerId, string lockerCode, string? requestId = null, int durationMs = 500);

    /// <summary>
    /// UC-S09: Phát lệnh khởi động lại vi điều khiển ESP32 từ xa (REBOOT).
    /// Topic: smartlocker/devices/{deviceCode}/cmd
    /// </summary>
    Task<bool> PublishRebootCommandAsync(string deviceCode, string? requestId = null);

    /// <summary>
    /// UC-S07: Kích hoạt chu trình chẩn đoán tự động phần cứng (SELF_TEST).
    /// Topic: smartlocker/devices/{deviceCode}/cmd
    /// </summary>
    Task<bool> PublishSelfTestCommandAsync(string deviceCode, string? requestId = null);

    /// <summary>
    /// Phát thông điệp thông báo chung xuống trạm (nếu cần).
    /// </summary>
    Task<bool> PublishMessageAsync(string topic, string payload, bool retain = false);
}
