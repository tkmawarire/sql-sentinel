using Microsoft.AspNetCore.SignalR;
using SqlServer.Profiler.Mcp.Dashboard.Services;
using SqlServer.Profiler.Mcp.Models;

namespace SqlServer.Profiler.Mcp.Dashboard.Hubs;

public interface IEventStreamClient
{
    Task ReceiveEvent(ProfilerEvent evt);
    Task ReceiveEventRate(double eventsPerSecond);
    Task SessionStateChanged(string sessionName, string newState);
}

public class EventStreamHub : Hub<IEventStreamClient>
{
    private readonly DashboardEventBroadcaster _broadcaster;
    private readonly ILogger<EventStreamHub> _logger;

    public EventStreamHub(DashboardEventBroadcaster broadcaster, ILogger<EventStreamHub> logger)
    {
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task SubscribeToSession(string sessionName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"session:{sessionName}");
        _broadcaster.EnsureSessionStreaming(sessionName);
        _logger.LogInformation("Client {ConnectionId} subscribed to session {Session}", Context.ConnectionId, sessionName);
    }

    public async Task UnsubscribeFromSession(string sessionName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"session:{sessionName}");
        _logger.LogInformation("Client {ConnectionId} unsubscribed from session {Session}", Context.ConnectionId, sessionName);
    }
}
