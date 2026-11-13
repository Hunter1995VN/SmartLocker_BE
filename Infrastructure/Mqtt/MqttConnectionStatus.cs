namespace Infrastructure.Mqtt;

using MQTTnet.Adapter;

/// <summary>
/// Last known state of the broker subscription, for /api/health. It lets operators tell a wrong
/// configuration, a blocked port, rejected credentials and a healthy link apart without host logs.
/// Never holds credentials or message payloads.
/// </summary>
public sealed class MqttConnectionStatus
{
    private readonly object _gate = new();
    private string _state = "starting";
    private string? _lastError;
    private int _consecutiveFailures;
    private DateTime? _lastAttemptAt, _lastConnectedAt, _lastMessageAt;

    public void MarkDisabled() => Set("disabled", null);
    public void MarkInvalidConfiguration() => Set("invalid-configuration", null);

    public void MarkConnecting()
    {
        lock (_gate) { _state = "connecting"; _lastAttemptAt = DateTime.UtcNow; }
    }

    public void MarkConnected()
    {
        lock (_gate) { _state = "connected"; _lastConnectedAt = DateTime.UtcNow; _lastError = null; _consecutiveFailures = 0; }
    }

    public void MarkDisconnected() => Set("disconnected", null);

    public void MarkFailed(Exception error)
    {
        lock (_gate) { _state = "error"; _lastError = Describe(error); _consecutiveFailures++; }
    }

    public void MarkMessageReceived()
    {
        lock (_gate) { _lastMessageAt = DateTime.UtcNow; }
    }

    public object Snapshot(MqttOptions options)
    {
        lock (_gate)
        {
            return new
            {
                enabled = options.Enabled,
                host = options.Host?.Trim(),
                port = options.Port,
                useTls = options.UseTls,
                hasCredentials = !string.IsNullOrWhiteSpace(options.Username),
                state = _state,
                consecutiveFailures = _consecutiveFailures,
                lastAttemptAt = _lastAttemptAt,
                lastConnectedAt = _lastConnectedAt,
                lastMessageAt = _lastMessageAt,
                lastError = _lastError,
            };
        }
    }

    private void Set(string state, string? error)
    {
        lock (_gate) { _state = state; _lastError = error; }
    }

    /// <summary>Exception chain as "Type: message" (connect result code when present), bounded in length.</summary>
    internal static string Describe(Exception error)
    {
        var parts = new List<string>();
        for (var e = error; e != null && parts.Count < 3; e = e.InnerException)
        {
            var text = e is MqttConnectingFailedException failed
                ? $"{e.GetType().Name}: {failed.ResultCode}"
                : $"{e.GetType().Name}: {e.Message}";
            parts.Add(text.Replace('\r', ' ').Replace('\n', ' '));
        }
        var joined = string.Join(" <- ", parts);
        return joined.Length > 400 ? joined[..400] + "..." : joined;
    }
}
