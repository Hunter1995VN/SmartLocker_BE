using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Infrastructure.Mqtt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hung.UseCases.Tests;

public class MqttStatusTests
{
    private static MqttOptions Options(int port, bool enabled = true) => new()
    {
        Enabled = enabled, Host = " 127.0.0.1 ", Port = port, UseTls = true,
        Username = "smartlocker-api", Password = "secret-value-should-not-leak", ReconnectDelaySeconds = 1,
    };

    [Fact]
    public void Snapshot_reports_configuration_without_credentials()
    {
        var json = JsonSerializer.Serialize(new MqttConnectionStatus().Snapshot(Options(8883)));

        Assert.Contains("\"host\":\"127.0.0.1\"", json);
        Assert.Contains("\"useTls\":true", json);
        Assert.Contains("\"hasCredentials\":true", json);
        Assert.DoesNotContain("secret-value-should-not-leak", json);
        Assert.DoesNotContain("smartlocker-api", json);
    }

    [Fact]
    public async Task Disabled_subscriber_reports_disabled()
    {
        var status = new MqttConnectionStatus();
        var service = new MqttSubscriberService(Microsoft.Extensions.Options.Options.Create(Options(8883, enabled: false)),
            NullLogger<MqttSubscriberService>.Instance, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), status);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("disabled", JsonSerializer.SerializeToElement(status.Snapshot(Options(8883, false))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Unreachable_broker_is_reported_as_error_with_reason()
    {
        // A port that nothing listens on: the connect fails like a blocked/closed broker port.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var status = new MqttConnectionStatus();
        var service = new MqttSubscriberService(Microsoft.Extensions.Options.Options.Create(Options(closedPort)),
            NullLogger<MqttSubscriberService>.Instance, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), status);

        await service.StartAsync(CancellationToken.None);
        JsonElement snapshot = default;
        for (var i = 0; i < 50; i++)
        {
            snapshot = JsonSerializer.SerializeToElement(status.Snapshot(Options(closedPort)));
            if (snapshot.GetProperty("state").GetString() == "error") break;
            await Task.Delay(100);
        }
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("error", snapshot.GetProperty("state").GetString());
        Assert.True(snapshot.GetProperty("consecutiveFailures").GetInt32() >= 1);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.GetProperty("lastError").GetString()));
        Assert.DoesNotContain("secret-value-should-not-leak", snapshot.GetRawText());
    }
}
