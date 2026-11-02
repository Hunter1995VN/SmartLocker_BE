namespace Infrastructure.Mqtt;

public sealed class MqttOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = $"smartlocker-be-{Guid.NewGuid():N}";
    public string Topic { get; set; } = "smartlocker/test";
    public string DoorEventTopic { get; set; } = "smartlocker/devices/+/door";
    public string HeartbeatTopic { get; set; } = "smartlocker/devices/+/heartbeat";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int ReconnectDelaySeconds { get; set; } = 5;
}
