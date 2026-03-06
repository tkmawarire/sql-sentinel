using Microsoft.AspNetCore.SignalR;
using SqlServer.Profiler.Mcp.Dashboard.Hubs;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;
using SqlServer.Profiler.Mcp.Utilities;

namespace SqlServer.Profiler.Mcp.Dashboard.Services;

public class DashboardMetricsService : IHostedService, IDisposable
{
    private readonly IProfilerService _profilerService;
    private readonly IWaitStatsService _waitStatsService;
    private readonly IHubContext<DiagnosticsHub, IDiagnosticsClient> _hubContext;
    private readonly ILogger<DashboardMetricsService> _logger;
    private Timer? _timer;

    public DashboardMetricsService(
        IProfilerService profilerService,
        IWaitStatsService waitStatsService,
        IHubContext<DiagnosticsHub, IDiagnosticsClient> hubContext,
        ILogger<DashboardMetricsService> logger)
    {
        _profilerService = profilerService;
        _waitStatsService = waitStatsService;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(PollAndBroadcast, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    private async void PollAndBroadcast(object? state)
    {
        try
        {
            var connectionString = ConnectionStringResolver.Resolve();

            // Get sessions list
            var sessions = await _profilerService.ListSessionsAsync(connectionString);
            await _hubContext.Clients.Group("diagnostics").ReceiveSessionList(sessions);

            // Get wait stats
            var waitStats = await _waitStatsService.GetWaitStatsAsync(connectionString);
            await _hubContext.Clients.Group("diagnostics").ReceiveWaitStats(waitStats);

            // Get active blocking
            var blockingResult = await _profilerService.GetConnectionInfoAsync(connectionString, "blocking");
            if (blockingResult.TryGetValue("activeBlocking", out var blockingObj) && blockingObj is List<ActiveBlockingInfo> blocking)
            {
                await _hubContext.Clients.Group("diagnostics").ReceiveActiveBlocking(blocking);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics polling failed");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
