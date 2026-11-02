namespace API.Services;

using API.Hubs;
using Application.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

public class LockerNotificationService : ILockerNotificationService
{
    private readonly IHubContext<LockerHub> _hubContext;
    private readonly ILogger<LockerNotificationService> _logger;

    public LockerNotificationService(IHubContext<LockerHub> hubContext, ILogger<LockerNotificationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyDoorStatusChangedAsync(Guid stationId, Guid lockerId, string lockerCode, string doorState, DateTimeOffset occurredAt)
    {
        var groupName = $"station_{stationId.ToString().ToLower()}";
        _logger.LogInformation("SignalR dispatching DoorStatusChanged to {GroupName}: Locker={LockerCode}, State={State}", groupName, lockerCode, doorState);

        await _hubContext.Clients.Group(groupName).SendAsync("DoorStatusChanged", new
        {
            stationId,
            lockerId,
            lockerCode,
            doorState,
            occurredAt = occurredAt.ToString("O")
        });
    }

    public Task NotifyStationStatusChangedAsync(Guid stationId, string connectivityStatus, DateTimeOffset occurredAt) =>
        _hubContext.Clients.Group($"station_{stationId.ToString().ToLowerInvariant()}")
            .SendAsync("StationStatusChanged", new { stationId, connectivityStatus, occurredAt });

    public async Task NotifyLockerUnlockedAsync(Guid stationId, Guid lockerId, string lockerCode)
    {
        var groupName = $"station_{stationId.ToString().ToLower()}";
        _logger.LogInformation("SignalR dispatching LockerUnlocked to {GroupName}: Locker={LockerCode}", groupName, lockerCode);

        await _hubContext.Clients.Group(groupName).SendAsync("LockerUnlocked", new
        {
            stationId,
            lockerId,
            lockerCode,
            timestamp = DateTimeOffset.UtcNow.ToString("O")
        });
    }
}
