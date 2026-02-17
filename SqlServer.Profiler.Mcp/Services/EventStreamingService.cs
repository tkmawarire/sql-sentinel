using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Tools;

namespace SqlServer.Profiler.Mcp.Services;

/// <summary>
/// Manages real-time event streaming by polling XE ring buffers and pushing new events to channels.
/// </summary>
public interface IEventStreamingService
{
    /// <summary>
    /// Start streaming events for a session. Returns a stream ID.
    /// </summary>
    string StartStreaming(string sessionName, string connectionString, EventFilters? filters = null);

    /// <summary>
    /// Stop a streaming session and clean up resources.
    /// </summary>
    void StopStreaming(string streamId);

    /// <summary>
    /// Get the channel reader for consuming events from a stream.
    /// </summary>
    ChannelReader<ProfilerEvent>? GetEventChannel(string streamId);

    /// <summary>
    /// Get info about all active streams.
    /// </summary>
    IReadOnlyList<StreamInfo> GetActiveStreams();
}

public record StreamInfo
{
    public required string StreamId { get; init; }
    public required string SessionName { get; init; }
    public DateTime StartedAt { get; init; }
    public long EventsEmitted { get; init; }
    public DateTime? LastEventAt { get; init; }
}

public class EventStreamingService : IEventStreamingService, IHostedService
{
    private const string SessionPrefix = "mcp_sentinel_";
    private const int ChannelCapacity = 10_000;
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TimestampOverlap = TimeSpan.FromSeconds(2);

    private readonly IProfilerService _profilerService;
    private readonly SessionConfigStore _configStore;
    private readonly ILogger<EventStreamingService> _logger;
    private readonly ConcurrentDictionary<string, StreamState> _streams = new();

    public EventStreamingService(
        IProfilerService profilerService,
        SessionConfigStore configStore,
        ILogger<EventStreamingService> logger)
    {
        _profilerService = profilerService;
        _configStore = configStore;
        _logger = logger;
    }

    public string StartStreaming(string sessionName, string connectionString, EventFilters? filters = null)
    {
        var streamId = $"{sessionName}_{Guid.NewGuid():N}";
        var channel = Channel.CreateBounded<ProfilerEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false
        });

        var cts = new CancellationTokenSource();
        var state = new StreamState
        {
            StreamId = streamId,
            SessionName = sessionName,
            ConnectionString = connectionString,
            Filters = filters ?? new EventFilters(),
            Channel = channel,
            Cts = cts,
            StartedAt = DateTime.UtcNow,
            LastSeenTimestamp = DateTime.UtcNow
        };

        if (!_streams.TryAdd(streamId, state))
            throw new InvalidOperationException($"Stream ID collision: {streamId}");

        // Start the polling loop as a background task
        _ = Task.Run(() => PollLoopAsync(state, cts.Token), cts.Token);

        _logger.LogInformation("Started streaming for session '{SessionName}', streamId={StreamId}", sessionName, streamId);
        return streamId;
    }

    public void StopStreaming(string streamId)
    {
        if (_streams.TryRemove(streamId, out var state))
        {
            state.Cts.Cancel();
            state.Channel.Writer.TryComplete();
            state.Cts.Dispose();
            _logger.LogInformation("Stopped streaming {StreamId}, emitted {Count} events", streamId, state.EventsEmitted);
        }
    }

    public ChannelReader<ProfilerEvent>? GetEventChannel(string streamId)
    {
        return _streams.TryGetValue(streamId, out var state) ? state.Channel.Reader : null;
    }

    public IReadOnlyList<StreamInfo> GetActiveStreams()
    {
        return _streams.Values.Select(s => new StreamInfo
        {
            StreamId = s.StreamId,
            SessionName = s.SessionName,
            StartedAt = s.StartedAt,
            EventsEmitted = s.EventsEmitted,
            LastEventAt = s.LastEventAt
        }).ToList();
    }

    private async Task PollLoopAsync(StreamState state, CancellationToken ct)
    {
        var fullSessionName = $"{SessionPrefix}{state.SessionName}";
        var currentInterval = DefaultPollInterval;
        long lastBufferSize = -1;
        // Track seen events by timestamp+hash to handle the overlap window
        var recentEventHashes = new HashSet<string>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Step 1: Cheap check — has the buffer size changed?
                    var bufferSize = await GetBufferSizeAsync(state.ConnectionString, fullSessionName, ct);

                    if (bufferSize == lastBufferSize)
                    {
                        // No change — back off slightly
                        currentInterval = TimeSpan.FromMilliseconds(
                            Math.Min(currentInterval.TotalMilliseconds * 1.5, MaxPollInterval.TotalMilliseconds));
                        await Task.Delay(currentInterval, ct);
                        continue;
                    }

                    lastBufferSize = bufferSize;

                    // Step 2: Buffer changed — fetch events since last seen timestamp (with overlap)
                    var config = _configStore.Get(state.SessionName);
                    var excludePatterns = config?.ExcludePatterns ?? NoisePatterns.Default;

                    var filters = state.Filters with
                    {
                        StartTime = state.LastSeenTimestamp - TimestampOverlap
                    };

                    var events = await _profilerService.GetEventsAsync(
                        state.ConnectionString, state.SessionName, filters, excludePatterns, ct);

                    // Step 3: Filter out already-seen events and emit new ones
                    var newEvents = new List<ProfilerEvent>();
                    var newHashes = new HashSet<string>();

                    foreach (var evt in events)
                    {
                        if (evt.EventTimestamp == null || evt.EventTimestamp <= state.LastSeenTimestamp - TimestampOverlap)
                            continue;

                        var hash = $"{evt.EventTimestamp:O}|{evt.SessionId}|{evt.EventName}|{evt.DurationUs}";
                        newHashes.Add(hash);

                        if (recentEventHashes.Contains(hash))
                            continue; // Already emitted in a previous poll

                        if (evt.EventTimestamp > state.LastSeenTimestamp)
                        {
                            newEvents.Add(evt);
                        }
                    }

                    // Update tracking state
                    recentEventHashes = newHashes; // Replace with current window

                    if (newEvents.Count > 0)
                    {
                        // Sort by timestamp and emit
                        foreach (var evt in newEvents.OrderBy(e => e.EventTimestamp))
                        {
                            if (!state.Channel.Writer.TryWrite(evt))
                                break; // Channel full (shouldn't happen with DropOldest)

                            state.EventsEmitted++;
                            state.LastEventAt = evt.EventTimestamp;
                        }

                        // Update last seen to the max timestamp
                        var maxTimestamp = newEvents.Max(e => e.EventTimestamp);
                        if (maxTimestamp.HasValue)
                            state.LastSeenTimestamp = maxTimestamp.Value;

                        // Reset poll interval — activity detected
                        currentInterval = MinPollInterval;
                    }
                    else
                    {
                        // No new events — back off
                        currentInterval = TimeSpan.FromMilliseconds(
                            Math.Min(currentInterval.TotalMilliseconds * 1.5, MaxPollInterval.TotalMilliseconds));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error polling stream {StreamId}, will retry", state.StreamId);
                    currentInterval = MaxPollInterval; // Back off on error
                }

                await Task.Delay(currentInterval, ct);
            }
        }
        finally
        {
            state.Channel.Writer.TryComplete();
        }
    }

    private static async Task<long> GetBufferSizeAsync(string connectionString, string fullSessionName, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("""
            SELECT ISNULL(DATALENGTH(st.target_data), 0)
            FROM sys.dm_xe_session_targets st
            INNER JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address
            WHERE s.name = @name AND st.target_name = 'ring_buffer'
            """, conn);
        cmd.Parameters.AddWithValue("@name", fullSessionName);
        cmd.CommandTimeout = 10;

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : Convert.ToInt64(result ?? 0L);
    }

    // IHostedService — lifecycle management
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop all active streams on shutdown
        foreach (var streamId in _streams.Keys.ToList())
        {
            StopStreaming(streamId);
        }
        return Task.CompletedTask;
    }

    private class StreamState
    {
        public required string StreamId { get; init; }
        public required string SessionName { get; init; }
        public required string ConnectionString { get; init; }
        public required EventFilters Filters { get; init; }
        public required Channel<ProfilerEvent> Channel { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime LastSeenTimestamp { get; set; }
        public long EventsEmitted { get; set; }
        public DateTime? LastEventAt { get; set; }
    }
}
