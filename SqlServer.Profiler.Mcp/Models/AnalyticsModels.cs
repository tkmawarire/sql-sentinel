namespace SqlServer.Profiler.Mcp.Models;

/// <summary>
/// Result of parsing a SQL statement, containing extracted tables, stored procedures, and operation type.
/// </summary>
public record SqlParseResult(List<string> Tables, List<string> StoredProcedures, string OperationType);

/// <summary>
/// Aggregated statistics for a table referenced in captured SQL events.
/// </summary>
public record TableStats
{
    public required string TableName { get; init; }
    public int CallCount { get; init; }
    public long TotalDurationUs { get; init; }
    public double AvgDurationUs { get; init; }
    public string AvgDurationFormatted { get; init; } = "";
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }
    public List<string> Operations { get; init; } = [];
    public string SampleSql { get; init; } = "";
}

/// <summary>
/// Aggregated statistics for a stored procedure referenced in captured SQL events.
/// </summary>
public record ProcedureStats
{
    public required string ProcedureName { get; init; }
    public int CallCount { get; init; }
    public long TotalDurationUs { get; init; }
    public double AvgDurationUs { get; init; }
    public string AvgDurationFormatted { get; init; } = "";
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }
    public string SampleSql { get; init; } = "";
}

/// <summary>
/// Aggregated statistics for a SQL operation type (SELECT, INSERT, UPDATE, etc.).
/// </summary>
public record OperationStats
{
    public required string OperationType { get; init; }
    public int CallCount { get; init; }
    public long TotalDurationUs { get; init; }
    public double AvgDurationUs { get; init; }
    public string AvgDurationFormatted { get; init; } = "";
    public long TotalReads { get; init; }
    public long TotalWrites { get; init; }
}

/// <summary>
/// Comparison between two time periods, highlighting queries that changed in performance.
/// </summary>
public record PeriodComparison
{
    public required PeriodSummary Period1 { get; init; }
    public required PeriodSummary Period2 { get; init; }
    public List<QueryDelta> SlowerQueries { get; init; } = [];
    public List<QueryDelta> FasterQueries { get; init; } = [];
    public List<string> NewQueryFingerprints { get; init; } = [];
    public List<string> DisappearedQueryFingerprints { get; init; } = [];
}

/// <summary>
/// Summary metrics for a specific time period.
/// </summary>
public record PeriodSummary
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public int TotalEvents { get; init; }
    public int UniqueQueries { get; init; }
    public long TotalDurationUs { get; init; }
    public double AvgDurationUs { get; init; }
    public long TotalReads { get; init; }
}

/// <summary>
/// Performance delta for a single query between two time periods.
/// </summary>
public record QueryDelta
{
    public required string QueryFingerprint { get; init; }
    public string SampleSql { get; init; } = "";
    public double Period1AvgDurationUs { get; init; }
    public double Period2AvgDurationUs { get; init; }
    public double ChangePercent { get; init; }
}

/// <summary>
/// Detected performance anomaly for a query, with severity classification.
/// </summary>
public record AnomalyResult
{
    public required string QueryFingerprint { get; init; }
    public string SampleSql { get; init; } = "";
    public required string Metric { get; init; }
    public double CurrentValue { get; init; }
    public double HistoricalAvg { get; init; }
    public double DeviationFactor { get; init; }
    public required string Severity { get; init; } // Info, Warning, Critical
}

/// <summary>
/// A single data point in a query performance trend over time.
/// </summary>
public record QueryTrendPoint
{
    public DateTime TimeBucket { get; init; }
    public int ExecutionCount { get; init; }
    public long AvgDurationUs { get; init; }
    public string AvgDurationFormatted { get; init; } = "";
    public long AvgReads { get; init; }
    public long AvgCpuUs { get; init; }
}
