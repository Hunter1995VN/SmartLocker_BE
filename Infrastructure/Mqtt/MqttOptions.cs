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
    public string DiagnosticsTopic { get; set; } = "smartlocker/devices/+/diagnostics";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Encrypt the broker connection (cloud brokers such as HiveMQ Cloud/EMQX on port 8883).
    /// The server certificate is validated against the OS trust store; there is no option to skip it.
    /// Off by default so the LAN broker on 1883 keeps working.
    /// </summary>
    public bool UseTls { get; set; }

    /// <summary>Applies host, port, TLS and credentials shared by the publisher and the subscriber.</summary>
    internal MQTTnet.Client.MqttClientOptionsBuilder ApplyConnection(MQTTnet.Client.MqttClientOptionsBuilder builder)
    {
        builder.WithTcpServer(Host.Trim(), Port);
        if (UseTls) builder.WithTlsOptions(tls => tls.UseTls());
        if (!string.IsNullOrWhiteSpace(Username)) builder.WithCredentials(Username, Password);
        return builder;
    }
}
