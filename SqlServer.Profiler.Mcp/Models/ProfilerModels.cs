using System.Text.Json.Serialization;

namespace SqlServer.Profiler.Mcp.Models;

/// <summary>
/// Response format options for tool outputs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResponseFormat
{
    Json,
    Markdown
}

/// <summary>
/// Event types that can be captured.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventType
{
    SqlBatchCompleted,
    RpcCompleted,
    SqlStatementCompleted,
    SpStatementCompleted,
    Attention,
    ErrorReported,
    Deadlock,
    BlockedProcess,
    LoginEvent,
    SchemaChange,
    Recompile,
    AutoStats,
    All
}

/// <summary>
/// Sort order options for event retrieval.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortOrder
{
    TimestampAsc,
    TimestampDesc,
    DurationAsc,
    DurationDesc,
    CpuDesc,
    ReadsDesc
}

/// <summary>
/// Configuration for a profiling session.
/// </summary>
public record SessionConfig
{
    public required string SessionName { get; init; }
    public List<string> Databases { get; init; } = [];
    public List<string> Applications { get; init; } = [];
    public List<string> Logins { get; init; } = [];
    public List<string> Hosts { get; init; } = [];
    public int MinDurationMs { get; init; }
    public List<string> IncludePatterns { get; init; } = [];
    public List<string> ExcludePatterns { get; init; } = [];
    public List<EventType> EventTypes { get; init; } = [EventType.SqlBatchCompleted, EventType.RpcCompleted];
    public int RingBufferMb { get; init; } = 50;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Represents a captured SQL Server event.
/// </summary>
public record ProfilerEvent
{
    public string EventName { get; init; } = "";
    public DateTime? EventTimestamp { get; init; }
    public long DurationUs { get; init; }
    public long CpuTimeUs { get; init; }
    public long LogicalReads { get; init; }
    public long PhysicalReads { get; init; }
    public long Writes { get; init; }
    public long RowCount { get; init; }
    public string SqlText { get; init; } = "";
    public string DatabaseName { get; init; } = "";
    public string ClientAppName { get; init; } = "";
    public string ClientHostname { get; init; } = "";
    public string LoginName { get; init; } = "";
    public int SessionId { get; init; }
    public long? TransactionId { get; init; }
    public int? RequestId { get; init; }
    public string? Result { get; init; }
    public int? ErrorNumber { get; init; }
    public string? ErrorMessage { get; init; }
    
    // Computed fields
    public string QueryFingerprint { get; init; } = "";
    public string DurationFormatted { get; init; } = "";
    
    // For deduplication
    public int ExecutionCount { get; set; } = 1;
    public long TotalDurationUs { get; set; }
    public long TotalCpuUs { get; set; }
    public long TotalReads { get; set; }
    public long MaxDurationUs { get; set; }
}

/// <summary>
/// Session information for listing active sessions.
/// </summary>
public record SessionInfo
{
    public required string SessionName { get; init; }
    public required string DisplayName { get; init; }
    public required string State { get; init; }
    public DateTime? CreateTime { get; init; }
    public long BufferUsedBytes { get; init; }
    public string BufferUsedFormatted { get; init; } = "";
}

/// <summary>
/// Statistics for a group of queries.
/// </summary>
public record QueryStats
{
    public required string Key { get; init; }
    public int Count { get; init; }
    public long TotalDurationUs { get; init; }
    public long TotalCpuUs { get; init; }
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }
    public long MaxDurationUs { get; init; }
    public double AvgDurationUs { get; init; }
    public string AvgDurationFormatted { get; init; } = "";
    public string MaxDurationFormatted { get; init; } = "";
    public string TotalDurationFormatted { get; init; } = "";
    public string SampleSql { get; init; } = "";
}

/// <summary>
/// Sequence entry for analyzing query execution order.
/// </summary>
public record SequenceEntry
{
    public int SequenceNumber { get; init; }
    public DateTime? Timestamp { get; init; }
    public string EventType { get; init; } = "";
    public string Database { get; init; } = "";
    public int SessionId { get; init; }
    public long? TransactionId { get; init; }
    public long DurationUs { get; init; }
    public string DurationFormatted { get; init; } = "";
    public long GapFromPreviousUs { get; init; }
    public string GapFormatted { get; init; } = "";
    public long CumulativeUs { get; init; }
    public string CumulativeFormatted { get; init; } = "";
    public string SqlText { get; init; } = "";
    public string QueryFingerprint { get; init; } = "";
}

/// <summary>
/// Connection information item.
/// </summary>
public record ConnectionInfoItem
{
    public required string Name { get; init; }
    public int Count { get; init; }
    public string? State { get; init; }
    public string? RecoveryModel { get; init; }
}

/// <summary>
/// Active session details.
/// </summary>
public record ActiveSession
{
    public int SessionId { get; init; }
    public string? LoginName { get; init; }
    public string? HostName { get; init; }
    public string? ProgramName { get; init; }
    public string? DatabaseName { get; init; }
    public string? Status { get; init; }
    public int CpuTime { get; init; }
    public long Reads { get; init; }
    public long Writes { get; init; }
    public DateTime? LoginTime { get; init; }
    public DateTime? LastRequestStartTime { get; init; }
}

/// <summary>
/// Represents a parsed deadlock event from xml_deadlock_report.
/// </summary>
public record DeadlockEvent
{
    public DateTime? EventTimestamp { get; init; }
    public string VictimSpid { get; init; } = "";
    public List<DeadlockProcess> Processes { get; init; } = [];
    public string RawXml { get; init; } = "";
}

/// <summary>
/// A process involved in a deadlock.
/// </summary>
public record DeadlockProcess
{
    public string ProcessId { get; init; } = "";
    public int Spid { get; init; }
    public string? LoginName { get; init; }
    public string? HostName { get; init; }
    public string? ApplicationName { get; init; }
    public string? DatabaseName { get; init; }
    public string? WaitResource { get; init; }
    public string? LockMode { get; init; }
    public int WaitTime { get; init; }
    public string? SqlText { get; init; }
    public bool IsVictim { get; init; }
}

/// <summary>
/// Represents a blocked process report event.
/// </summary>
public record BlockingEvent
{
    public DateTime? EventTimestamp { get; init; }
    public BlockedProcessInfo BlockedProcess { get; init; } = new();
    public BlockingProcessInfo BlockingProcess { get; init; } = new();
    public string RawXml { get; init; } = "";
}

/// <summary>
/// Details of the blocked process.
/// </summary>
public record BlockedProcessInfo
{
    public int Spid { get; init; }
    public string? WaitResource { get; init; }
    public int WaitTimeMs { get; init; }
    public string? LoginName { get; init; }
    public string? HostName { get; init; }
    public string? DatabaseName { get; init; }
    public string? SqlText { get; init; }
    public string? LockMode { get; init; }
}

/// <summary>
/// Details of the blocking process.
/// </summary>
public record BlockingProcessInfo
{
    public int Spid { get; init; }
    public string? LoginName { get; init; }
    public string? HostName { get; init; }
    public string? DatabaseName { get; init; }
    public string? SqlText { get; init; }
    public string? Status { get; init; }
}

/// <summary>
/// A wait statistics entry from sys.dm_os_wait_stats.
/// </summary>
public record WaitStatEntry
{
    public required string WaitType { get; init; }
    public string WaitCategory { get; init; } = "Other";
    public long WaitingTasksCount { get; init; }
    public long WaitTimeMs { get; init; }
    public long MaxWaitTimeMs { get; init; }
    public long SignalWaitTimeMs { get; init; }
    public string WaitTimeFormatted { get; init; } = "";
    public string MaxWaitTimeFormatted { get; init; } = "";
}

/// <summary>
/// Currently active blocking from DMVs.
/// </summary>
public record ActiveBlockingInfo
{
    public int BlockedSpid { get; init; }
    public int BlockingSpid { get; init; }
    public string? WaitType { get; init; }
    public long WaitTimeMs { get; init; }
    public string? WaitResource { get; init; }
    public string? BlockedLoginName { get; init; }
    public string? BlockedDatabase { get; init; }
    public string? BlockedSqlText { get; init; }
}

/// <summary>
/// Comprehensive health check result.
/// </summary>
public record HealthCheckResult
{
    public DateTime CheckedAt { get; init; } = DateTime.UtcNow;
    public List<QueryStats> SlowestQueries { get; init; } = [];
    public List<DeadlockEvent> RecentDeadlocks { get; init; } = [];
    public List<BlockingEvent> RecentBlocking { get; init; } = [];
    public List<WaitStatEntry> TopWaitStats { get; init; } = [];
    public List<ActiveBlockingInfo> ActiveBlocking { get; init; } = [];
    public List<HealthInsight> Insights { get; init; } = [];
}

/// <summary>
/// An actionable insight from diagnostic analysis.
/// </summary>
public record HealthInsight
{
    public required string Severity { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }
}
