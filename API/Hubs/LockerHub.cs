namespace API.Hubs;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

public class LockerHub : Hub
{
    private readonly ILogger<LockerHub> _logger;

    public LockerHub(ILogger<LockerHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Kiosk hoặc Admin đăng ký lắng nghe sự kiện của một Station cụ thể.
    /// </summary>
    public async Task JoinStation(string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId)) return;
        var groupName = $"station_{stationId.ToLower()}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("SignalR: Client {ConnectionId} joined {GroupName}", Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Rời khỏi nhóm lắng nghe của trạm.
    /// </summary>
    public async Task LeaveStation(string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId)) return;
        var groupName = $"station_{stationId.ToLower()}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("SignalR: Client {ConnectionId} left {GroupName}", Context.ConnectionId, groupName);
    }
}
