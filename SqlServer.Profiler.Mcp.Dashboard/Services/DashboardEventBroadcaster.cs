using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using SqlServer.Profiler.Mcp.Dashboard.Hubs;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;
using SqlServer.Profiler.Mcp.Utilities;

namespace SqlServer.Profiler.Mcp.Dashboard.Services;

public class DashboardEventBroadcaster : IHostedService
{
    private readonly IEventStreamingService _streaming;
    private readonly IHubContext<EventStreamHub, IEventStreamClient> _hubContext;
    private readonly ILogger<DashboardEventBroadcaster> _logger;
    private readonly ConcurrentDictionary<string, (string StreamId, CancellationTokenSource Cts)> _subscriptions = new();
    private readonly ConcurrentDictionary<string, int> _eventRateCounters = new();
    private Timer? _rateTimer;

    public DashboardEventBroadcaster(
        IEventStreamingService streaming,
        IHubContext<EventStreamHub, IEventStreamClient> hubContext,
        ILogger<DashboardEventBroadcaster> logger)
    {
        _streaming = streaming;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _rateTimer = new Timer(BroadcastRates, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _rateTimer?.Dispose();
        foreach (var sub in _subscriptions.Values)
        {
            sub.Cts.Cancel();
            _streaming.StopStreaming(sub.StreamId);
        }
        _subscriptions.Clear();
        return Task.CompletedTask;
    }

    public void EnsureSessionStreaming(string sessionName)
    {
        if (_subscriptions.ContainsKey(sessionName))
            return;

        try
        {
            var connectionString = ConnectionStringResolver.Resolve();
            var streamId = _streaming.StartStreaming(sessionName, connectionString);
            var cts = new CancellationTokenSource();

            if (_subscriptions.TryAdd(sessionName, (streamId, cts)))
            {
                _eventRateCounters.TryAdd(sessionName, 0);
                _ = PumpEventsAsync(sessionName, streamId, cts.Token);
                _logger.LogInformation("Started streaming session {Session} with stream {StreamId}", sessionName, streamId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start streaming for session {Session}", sessionName);
        }
    }

    private async Task PumpEventsAsync(string sessionName, string streamId, CancellationToken ct)
    {
        var channel = _streaming.GetEventChannel(streamId);
        if (channel == null) return;

        try
        {
            await foreach (var evt in channel.ReadAllAsync(ct))
            {
                await _hubContext.Clients.Group($"session:{sessionName}").ReceiveEvent(evt);
                _eventRateCounters.AddOrUpdate(sessionName, 1, (_, count) => count + 1);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event pump error for session {Session}", sessionName);
        }
    }

    private void BroadcastRates(object? state)
    {
        foreach (var kvp in _eventRateCounters)
        {
            var count = kvp.Value;
            _eventRateCounters.TryUpdate(kvp.Key, 0, count);
            _ = _hubContext.Clients.Group($"session:{kvp.Key}").ReceiveEventRate(count);
        }
    }
}
