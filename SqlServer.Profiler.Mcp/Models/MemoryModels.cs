using System.Text.Json.Serialization;

namespace SqlServer.Profiler.Mcp.Models;

/// <summary>
/// A capture snapshot — the top-level memory unit.
/// Created automatically when events are retrieved from a profiling session.
/// </summary>
public record CaptureMemory
{
    public required string Id { get; init; }
    public required string ServerName { get; init; }
    public required string SessionName { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
    public DateTime? TimeRangeStart { get; init; }
    public DateTime? TimeRangeEnd { get; init; }

    // Summary stats
    public int TotalEvents { get; init; }
    public int UniqueQueries { get; init; }
    public long TotalDurationUs { get; init; }
    public long TotalCpuUs { get; init; }
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }

    // Capture context
    public List<string> DatabasesObserved { get; init; } = [];
    public List<string> ApplicationsObserved { get; init; } = [];
    public List<string> LoginsObserved { get; init; } = [];

    // Top findings for quick agent scanning
    public List<MemoryInsight> Insights { get; init; } = [];
    public List<QuerySummary> TopQueriesByDuration { get; init; } = [];

    // Diagnostic counts
    public int DeadlockCount { get; init; }
    public int BlockingEventCount { get; init; }

    // Agent annotations
    public List<string> Tags { get; set; } = [];
    public string? AgentNote { get; set; }

    // Lifecycle
    public string State { get; set; } = "active";
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>
/// Lightweight query reference stored in CaptureMemory.TopQueriesByDuration.
/// </summary>
public record QuerySummary
{
    public required string QueryFingerprint { get; init; }
    public string SqlPreview { get; init; } = "";
    public int ExecutionCount { get; init; }
    public long TotalDurationUs { get; init; }
    public double AvgDurationUs { get; init; }
    public long MaxDurationUs { get; init; }
    public long TotalReads { get; init; }
}

/// <summary>
/// Per-fingerprint performance history across captures.
/// Stored as individual JSON files for O(1) lookup.
/// </summary>
public record QueryMemory
{
    public required string QueryFingerprint { get; init; }
    public required string ServerName { get; init; }
    public string NormalizedSqlPreview { get; init; } = "";
    public string SampleSql { get; init; } = "";

    // Cumulative stats
    public int TotalExecutions { get; init; }
    public long TotalDurationUs { get; init; }
    public long TotalCpuUs { get; init; }
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }
    public long MaxDurationUs { get; init; }
    public long MinDurationUs { get; init; }
    public double AvgDurationUs { get; init; }

    // Context
    public List<string> DatabasesSeen { get; init; } = [];
    public List<string> ApplicationsSeen { get; init; } = [];

    // Time-series observations (capped at MaxQueryObservations)
    public List<QueryObservation> Observations { get; init; } = [];

    public DateTime FirstSeenAt { get; init; }
    public DateTime LastSeenAt { get; init; }
    public int CaptureCount { get; init; }
}

/// <summary>
/// A single observation of a query fingerprint within a capture.
/// </summary>
public record QueryObservation
{
    public required string CaptureId { get; init; }
    public DateTime ObservedAt { get; init; }
    public int ExecutionCount { get; init; }
    public long AvgDurationUs { get; init; }
    public long MaxDurationUs { get; init; }
    public long AvgReads { get; init; }
    public long AvgCpuUs { get; init; }
}

/// <summary>
/// An insight or finding worth remembering.
/// </summary>
public record MemoryInsight
{
    public required string Severity { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public string? QueryFingerprint { get; init; }
    public string? Detail { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Memory system statistics.
/// </summary>
public record MemoryStats
{
    public int TotalCaptures { get; init; }
    public int TotalQueryFingerprints { get; init; }
    public long DiskUsageBytes { get; init; }
    public DateTime? OldestCapture { get; init; }
    public DateTime? NewestCapture { get; init; }
    public Dictionary<string, int> CapturesPerServer { get; init; } = new();
}

/// <summary>
/// Persisted memory configuration.
/// </summary>
public record MemoryConfig
{
    public int DefaultTtlDays { get; init; } = 30;
    public int TaggedTtlDays { get; init; } = 90;
    public int MaxMemoryMb { get; init; } = 100;
    public int MaxQueryObservations { get; init; } = 50;
    public int MaxCaptureTopQueries { get; init; } = 10;
}
