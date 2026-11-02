using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Infrastructure.Mqtt;

// Each door message uses a fresh DI scope for its scoped DbContext.
public sealed class MqttSubscriberService(
    IOptions<MqttOptions> options,
    ILogger<MqttSubscriberService> logger,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("MQTT is disabled. Set Mqtt:Enabled to true to enable it.");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.Host) || settings.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.Topic)
            || settings.ReconnectDelaySeconds < 1)
        {
            logger.LogError("Invalid MQTT configuration; subscriber was not started.");
            return;
        }

        var factory = new MqttFactory();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Host, settings.Port)
            .WithClientId(settings.ClientId)
            .WithCleanSession()
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrWhiteSpace(settings.Username))
            builder.WithCredentials(settings.Username, settings.Password);
        var clientOptions = builder.Build();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var client = factory.CreateMqttClient();
                client.ApplicationMessageReceivedAsync += async args =>
                {
                    if (args.ApplicationMessage.Topic != settings.Topic)
                    {
                        try
                        {
                            if (args.ApplicationMessage.PayloadSegment.Count > 4096)
                            {
                                logger.LogWarning("Ignored oversized MQTT door event.");
                                return;
                            }
                            using var scope = scopeFactory.CreateScope();
                            if (args.ApplicationMessage.Topic.EndsWith("/heartbeat", StringComparison.Ordinal))
                                await scope.ServiceProvider.GetRequiredService<HeartbeatEventHandler>().HandleAsync(
                                    args.ApplicationMessage.Topic, args.ApplicationMessage.ConvertPayloadToString(), args.ApplicationMessage.Retain, stoppingToken);
                            else
                                await scope.ServiceProvider.GetRequiredService<DoorEventHandler>().HandleAsync(
                                    args.ApplicationMessage.Topic, args.ApplicationMessage.ConvertPayloadToString(), args.ApplicationMessage.Retain, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            logger.LogError("MQTT door processing failed ({ErrorType}); event was not saved.", ex.GetType().Name);
                        }
                        return;
                    }
                    // Bound the test payload log and keep it on one line.
                    var payload = args.ApplicationMessage.ConvertPayloadToString();
                    if (payload.Length > 1024) payload = payload[..1024] + "...";
                    logger.LogInformation("MQTT received: {Topic} -> {Payload}",
                        args.ApplicationMessage.Topic,
                        payload.Replace("\r", "\\r").Replace("\n", "\\n"));
                };

                try
                {
                    await client.ConnectAsync(clientOptions, stoppingToken);
                    var subscription = await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
                        .WithTopicFilter(filter => filter.WithTopic(settings.Topic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(filter => filter.WithTopic(settings.DoorEventTopic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .WithTopicFilter(filter => filter.WithTopic(settings.HeartbeatTopic)
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
                        .Build(), stoppingToken);
                    if (subscription.Items.Any(item => (int)item.ResultCode >= 128))
                        throw new InvalidOperationException("Broker rejected the MQTT subscription.");

                    logger.LogInformation("MQTT connected to {Host}:{Port}; subscribed to {Topic}",
                        settings.Host, settings.Port, settings.Topic);
                    logger.LogInformation("MQTT door subscription: {Topic}", settings.DoorEventTopic);
                    while (client.IsConnected)
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    logger.LogWarning("MQTT disconnected; reconnecting in {Delay} seconds.", settings.ReconnectDelaySeconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("MQTT connection/subscription failed ({ErrorType}); retrying in {Delay} seconds.",
                        ex.GetType().Name, settings.ReconnectDelaySeconds);
                }
                finally
                {
                    if (client.IsConnected)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        try
                        {
                            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), timeout.Token);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug("MQTT disconnect ended with {ErrorType}.", ex.GetType().Name);
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(settings.ReconnectDelaySeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
