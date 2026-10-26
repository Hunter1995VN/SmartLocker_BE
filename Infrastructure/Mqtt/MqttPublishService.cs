namespace Infrastructure.Mqtt;

using System.Text.Json;
using Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

public class MqttPublishService : IMqttPublishService, IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly ILogger<MqttPublishService> _logger;
    private readonly MqttFactory _factory = new();
    private IMqttClient? _client;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public MqttPublishService(IOptions<MqttOptions> options, ILogger<MqttPublishService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    private async Task<IMqttClient?> GetConnectedClientAsync()
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("MqttPublishService: MQTT is disabled in config.");
            return null;
        }

        await _semaphore.WaitAsync();
        try
        {
            if (_client != null && _client.IsConnected)
            {
                return _client;
            }

            _client?.Dispose();
            _client = _factory.CreateMqttClient();

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_options.Host, _options.Port)
                .WithClientId($"smartlocker-publisher-{Guid.NewGuid():N}")
                .WithCleanSession()
                .WithTimeout(TimeSpan.FromSeconds(5));

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                builder.WithCredentials(_options.Username, _options.Password);
            }

            var clientOptions = builder.Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _client.ConnectAsync(clientOptions, cts.Token);
            _logger.LogInformation("MqttPublishService connected to {Host}:{Port}", _options.Host, _options.Port);
            return _client;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MqttPublishService failed to connect to broker at {Host}:{Port}", _options.Host, _options.Port);
            return null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> PublishUnlockCommandAsync(string deviceCode, Guid lockerId, string lockerCode, string? requestId = null, int durationMs = 500)
    {
        var targetTopic = $"smartlocker/devices/{deviceCode}/cmd";
        var payloadObj = new
        {
            command = "UNLOCK",
            lockerId = lockerId.ToString(),
            lockerCode = lockerCode,
            requestId = requestId ?? Guid.NewGuid().ToString("N"),
            durationMs = durationMs,
            expiresAt = DateTime.UtcNow.AddSeconds(30).ToString("O")
        };

        var json = JsonSerializer.Serialize(payloadObj);
        _logger.LogInformation("Publishing UNLOCK command to {Topic}: {Payload}", targetTopic, json);

        return await PublishMessageAsync(targetTopic, json, retain: false);
    }

    public async Task<bool> PublishMessageAsync(string topic, string payload, bool retain = false)
    {
        try
        {
            var client = await GetConnectedClientAsync();
            if (client == null || !client.IsConnected)
            {
                _logger.LogWarning("Cannot publish MQTT message: client not connected to broker.");
                return false;
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(retain)
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await client.PublishAsync(message, cts.Token);
            return result.IsSuccess;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish MQTT message to topic {Topic}", topic);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client != null)
        {
            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync();
                }
            }
            catch { }
            _client.Dispose();
        }
        _semaphore.Dispose();
    }
}
