using Microsoft.AspNetCore.SignalR;
using SqlServer.Profiler.Mcp.Models;

namespace SqlServer.Profiler.Mcp.Dashboard.Hubs;

public interface IDiagnosticsClient
{
    Task ReceiveActiveBlocking(List<ActiveBlockingInfo> blocking);
    Task ReceiveWaitStats(List<WaitStatEntry> waitStats);
    Task ReceiveSessionList(List<SessionInfo> sessions);
}

public class DiagnosticsHub : Hub<IDiagnosticsClient>
{
    private readonly ILogger<DiagnosticsHub> _logger;

    public DiagnosticsHub(ILogger<DiagnosticsHub> logger)
    {
        _logger = logger;
    }

    public async Task SubscribeDiagnostics()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "diagnostics");
        _logger.LogInformation("Client {ConnectionId} subscribed to diagnostics", Context.ConnectionId);
    }

    public async Task UnsubscribeDiagnostics()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "diagnostics");
    }
}
