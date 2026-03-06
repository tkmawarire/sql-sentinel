using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;
using Microsoft.Extensions.Logging;
using SqlServer.Profiler.Mcp.Utilities;

namespace SqlServer.Profiler.Mcp.Tools;

/// <summary>
/// MCP Tools for advanced SQL query analytics: table hotspot analysis, procedure stats,
/// operation breakdown, period comparison, anomaly detection, and query trend tracking.
/// </summary>
[McpServerToolType]
public class AnalyticsTools
{
    private readonly IProfilerService _profilerService;
    private readonly ISqlParsingService _sqlParsingService;
    private readonly ITrendAnalysisService _trendAnalysisService;
    private readonly IQueryFingerprintService _fingerprintService;
    private readonly SessionConfigStore _configStore;
    private readonly ILogger<AnalyticsTools> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="AnalyticsTools"/>.
    /// </summary>
    public AnalyticsTools(
        IProfilerService profilerService,
        ISqlParsingService sqlParsingService,
        ITrendAnalysisService trendAnalysisService,
        IQueryFingerprintService fingerprintService,
        SessionConfigStore configStore,
        ILogger<AnalyticsTools> logger)
    {
        _profilerService = profilerService;
        _sqlParsingService = sqlParsingService;
        _trendAnalysisService = trendAnalysisService;
        _fingerprintService = fingerprintService;
        _configStore = configStore;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tool 1: Stats by Table
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get aggregate statistics grouped by SQL Server table.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_get_stats_by_table")]
    [Description("""
        Get aggregate statistics grouped by SQL Server table.

        Parses SQL text from captured events to extract table references (FROM, JOIN, INSERT INTO, UPDATE, DELETE FROM, MERGE),
        then aggregates performance metrics per table.
        Helps identify which tables are hotspots for reads, writes, or slow queries.
        """)]
    public async Task<string> GetStatsByTable(
        [Description("Name of the profiling session")] string sessionName,
        [Description("Analysis window start (ISO format)")] string? startTime = null,
        [Description("Analysis window end (ISO format)")] string? endTime = null,
        [Description("Return top N results")] int topN = 20,
        [Description("Output format: Json or Markdown")] string responseFormat = "Json")
    {
        try
        {
            var connectionString = ConnectionStringResolver.Resolve();
            var config = _configStore.Get(sessionName);
            var excludePatterns = config?.ExcludePatterns ?? NoisePatterns.Default;

            var filters = new EventFilters
            {
                StartTime = ParseDateTime(startTime),
                EndTime = ParseDateTime(endTime)
            };

            var events = await _profilerService.GetEventsAsync(connectionString, sessionName, filters, excludePatterns);

            if (events.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No events found in the specified time range",
                    stats = Array.Empty<object>()
                }, JsonOptions.Default);
            }

            // Accumulate per-table metrics
            var tableMap = new Dictionary<string, (int callCount, long totalDuration, long totalReads, long totalWrites, HashSet<string> operations, string sampleSql)>(StringComparer.OrdinalIgnoreCase);

            foreach (var evt in events)
            {
                var tables = _sqlParsingService.ExtractTableReferences(evt.SqlText);
                if (tables.Count == 0)
                    continue;

                var operation = _sqlParsingService.ClassifyOperation(evt.SqlText);

                foreach (var table in tables)
                {
                    if (!tableMap.TryGetValue(table, out var entry))
                    {
                        entry = (0, 0L, 0L, 0L, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            evt.SqlText.Length > 200 ? evt.SqlText[..200] : evt.SqlText);
                        tableMap[table] = entry;
                    }

                    tableMap[table] = (
                        entry.callCount + 1,
                        entry.totalDuration + evt.DurationUs,
                        entry.totalReads + evt.LogicalReads,
                        entry.totalWrites + evt.Writes,
                        entry.operations,
                        entry.sampleSql
                    );
                    tableMap[table].operations.Add(operation);
                }
            }

            var stats = tableMap
                .Select(kvp =>
                {
                    var avgDuration = kvp.Value.callCount > 0
                        ? kvp.Value.totalDuration / (double)kvp.Value.callCount
                        : 0.0;
                    return new TableStats
                    {
                        TableName = kvp.Key,
                        CallCount = kvp.Value.callCount,
                        TotalDurationUs = kvp.Value.totalDuration,
                        AvgDurationUs = avgDuration,
                        AvgDurationFormatted = ProfilerService.FormatDuration((long)avgDuration),
                        TotalReads = kvp.Value.totalReads,
                        TotalWrites = kvp.Value.totalWrites,
                        Operations = kvp.Value.operations.OrderBy(o => o).ToList(),
                        SampleSql = kvp.Value.sampleSql
                    };
                })
                .OrderByDescending(s => s.TotalDurationUs)
                .Take(topN)
                .ToList();

            var result = new
            {
                totalEvents = events.Count,
                uniqueTables = tableMap.Count,
                topN,
                stats
            };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatTableStatsMarkdown(result.totalEvents, result.uniqueTables, topN, stats);
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ErrorSanitizer.Sanitize(ex, _logger)
            }, JsonOptions.Default);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tool 2: Stats by Procedure
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get aggregate statistics grouped by stored procedure.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_get_stats_by_procedure")]
    [Description("""
        Get aggregate statistics grouped by stored procedure.

        Detects stored procedure calls from SQL text (EXEC, EXECUTE patterns),
        then aggregates performance metrics per procedure.
        Queries not associated with a stored procedure are grouped under '(ad-hoc SQL)'.
        """)]
    public async Task<string> GetStatsByProcedure(
        [Description("Name of the profiling session")] string sessionName,
        [Description("Analysis window start (ISO format)")] string? startTime = null,
        [Description("Analysis window end (ISO format)")] string? endTime = null,
        [Description("Return top N results")] int topN = 20,
        [Description("Output format: Json or Markdown")] string responseFormat = "Json")
    {
        try
        {
            var connectionString = ConnectionStringResolver.Resolve();
            var config = _configStore.Get(sessionName);
            var excludePatterns = config?.ExcludePatterns ?? NoisePatterns.Default;

            var filters = new EventFilters
            {
                StartTime = ParseDateTime(startTime),
                EndTime = ParseDateTime(endTime)
            };

            var events = await _profilerService.GetEventsAsync(connectionString, sessionName, filters, excludePatterns);

            if (events.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No events found in the specified time range",
                    stats = Array.Empty<object>()
                }, JsonOptions.Default);
            }

            // Accumulate per-procedure metrics
            var procMap = new Dictionary<string, (int callCount, long totalDuration, long totalReads, long totalWrites, string sampleSql)>(StringComparer.OrdinalIgnoreCase);

            foreach (var evt in events)
            {
                var procs = _sqlParsingService.ExtractStoredProcedures(evt.SqlText);
                var keys = procs.Count > 0 ? procs : new List<string> { "(ad-hoc SQL)" };

                foreach (var key in keys)
                {
                    var sampleSql = evt.SqlText.Length > 200 ? evt.SqlText[..200] : evt.SqlText;
                    if (!procMap.TryGetValue(key, out var entry))
                    {
                        entry = (0, 0L, 0L, 0L, sampleSql);
                        procMap[key] = entry;
                    }

                    procMap[key] = (
                        entry.callCount + 1,
                        entry.totalDuration + evt.DurationUs,
                        entry.totalReads + evt.LogicalReads,
                        entry.totalWrites + evt.Writes,
                        entry.sampleSql
                    );
                }
            }

            var stats = procMap
                .Select(kvp =>
                {
                    var avgDuration = kvp.Value.callCount > 0
                        ? kvp.Value.totalDuration / (double)kvp.Value.callCount
                        : 0.0;
                    return new ProcedureStats
                    {
                        ProcedureName = kvp.Key,
                        CallCount = kvp.Value.callCount,
                        TotalDurationUs = kvp.Value.totalDuration,
                        AvgDurationUs = avgDuration,
                        AvgDurationFormatted = ProfilerService.FormatDuration((long)avgDuration),
                        TotalReads = kvp.Value.totalReads,
                        TotalWrites = kvp.Value.totalWrites,
                        SampleSql = kvp.Value.sampleSql
                    };
                })
                .OrderByDescending(s => s.TotalDurationUs)
                .Take(topN)
                .ToList();

            var result = new
            {
                totalEvents = events.Count,
                uniqueProcedures = procMap.Count,
                topN,
                stats
            };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatProcedureStatsMarkdown(result.totalEvents, result.uniqueProcedures, topN, stats);
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ErrorSanitizer.Sanitize(ex, _logger)
            }, JsonOptions.Default);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tool 3: Stats by Operation
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get aggregate statistics grouped by SQL operation type.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_get_stats_by_operation")]
    [Description("""
        Get aggregate statistics grouped by SQL operation type.

        Classifies each captured event as SELECT, INSERT, UPDATE, DELETE, EXEC, DDL, MERGE, or UNKNOWN,
        then aggregates performance metrics per operation type.
        """)]
    public async Task<string> GetStatsByOperation(
        [Description("Name of the profiling session")] string sessionName,
        [Description("Analysis window start (ISO format)")] string? startTime = null,
        [Description("Analysis window end (ISO format)")] string? endTime = null,
        [Description("Return top N results")] int topN = 20,
        [Description("Output format: Json or Markdown")] string responseFormat = "Json")
    {
        try
        {
            var connectionString = ConnectionStringResolver.Resolve();
            var config = _configStore.Get(sessionName);
            var excludePatterns = config?.ExcludePatterns ?? NoisePatterns.Default;

            var filters = new EventFilters
            {
                StartTime = ParseDateTime(startTime),
                EndTime = ParseDateTime(endTime)
            };

            var events = await _profilerService.GetEventsAsync(connectionString, sessionName, filters, excludePatterns);

            if (events.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No events found in the specified time range",
                    stats = Array.Empty<object>()
                }, JsonOptions.Default);
            }

            // Accumulate per-operation metrics
            var opMap = new Dictionary<string, (int callCount, long totalDuration, long totalReads, long totalWrites)>(StringComparer.OrdinalIgnoreCase);

            foreach (var evt in events)
            {
                var operation = _sqlParsingService.ClassifyOperation(evt.SqlText);

                if (!opMap.TryGetValue(operation, out var entry))
                {
                    entry = (0, 0L, 0L, 0L);
                    opMap[operation] = entry;
                }

                opMap[operation] = (
                    entry.callCount + 1,
                    entry.totalDuration + evt.DurationUs,
                    entry.totalReads + evt.LogicalReads,
                    entry.totalWrites + evt.Writes
                );
            }

            var stats = opMap
                .Select(kvp =>
                {
                    var avgDuration = kvp.Value.callCount > 0
                        ? kvp.Value.totalDuration / (double)kvp.Value.callCount
                        : 0.0;
                    return new OperationStats
                    {
                        OperationType = kvp.Key,
                        CallCount = kvp.Value.callCount,
                        TotalDurationUs = kvp.Value.totalDuration,
                        AvgDurationUs = avgDuration,
                        AvgDurationFormatted = ProfilerService.FormatDuration((long)avgDuration),
                        TotalReads = kvp.Value.totalReads,
                        TotalWrites = kvp.Value.totalWrites
                    };
                })
                .OrderByDescending(s => s.TotalDurationUs)
                .Take(topN)
                .ToList();

            var result = new
            {
                totalEvents = events.Count,
                uniqueOperations = opMap.Count,
                topN,
                stats
            };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatOperationStatsMarkdown(result.totalEvents, result.uniqueOperations, stats);
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ErrorSanitizer.Sanitize(ex, _logger)
            }, JsonOptions.Default);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tool 4: Compare Periods
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compare query performance between two time periods.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_compare_periods")]
    [Description("""
        Compare query performance between two time periods.

        Identifies queries that got slower, faster, appeared, or disappeared between two time windows.
        Useful for before/after deployment comparisons or time-of-day analysis.
        """)]
    public async Task<string> ComparePeriods(
        [Description("Name of the profiling session")] string sessionName,
        [Description("Period 1 start (ISO format)")] string period1Start,
        [Description("Period 1 end (ISO format)")] string period1End,
        [Description("Period 2 start (ISO format)")] string period2Start,
        [Description("Period 2 end (ISO format)")] string period2End,
        [Description("Output format: Json or Markdown")] string responseFormat = "Json")
    {
        try
        {
            if (!DateTime.TryParse(period1Start, out var p1Start))
                return JsonSerializer.Serialize(new { success = false, error = $"Invalid period1Start: '{period1Start}'. Use ISO format." }, JsonOptions.Default);
            if (!DateTime.TryParse(period1End, out var p1End))
                return JsonSerializer.Serialize(new { success = false, error = $"Invalid period1End: '{period1End}'. Use ISO format." }, JsonOptions.Default);
            if (!DateTime.TryParse(period2Start, out var p2Start))
                return JsonSerializer.Serialize(new { success = false, error = $"Invalid period2Start: '{period2Start}'. Use ISO format." }, JsonOptions.Default);
            if (!DateTime.TryParse(period2End, out var p2End))
                return JsonSerializer.Serialize(new { success = false, error = $"Invalid period2End: '{period2End}'. Use ISO format." }, JsonOptions.Default);

            var connectionString = ConnectionStringResolver.Resolve();

            var comparison = await _trendAnalysisService.ComparePeriodsAsync(
                connectionString, sessionName,
                p1Start, p1End,
                p2Start, p2End);

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatComparisonMarkdown(comparison);
            }

            return JsonSerializer.Serialize(comparison, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ErrorSanitizer.Sanitize(ex, _logger)
            }, JsonOptions.Default);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tool 5: Detect Anomalies
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detect query performance anomalies by comparing current session data against historical baselines.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_detect_anomalies")]
    [Description("""
        Detect query performance anomalies by comparing current session data against historical baselines.

        Compares current query metrics against stored historical averages from the memory system.
        Flags queries whose current performance deviates significantly from their historical baseline.
        Requires memory to be enabled and historical captures to exist for the server.
        """)]
    public async Task<string> DetectAnomalies(
        [Description("Name of the profiling session")] string sessionName,
        [Description("How many times above historical average to flag as anomaly (default 2.0 = 2x worse)")] double thresholdMultiplier = 2.0,
        [Description("Output format: Json or Markdown")] string responseFormat = "Json")
    {
        try
        {
            var connectionString = ConnectionStringResolver.Resolve();
            var serverName = MemoryService.ExtractServerName(connectionString);

            var anomalies = await _trendAnalysisService.DetectAnomaliesAsync(
                serverName, connectionString, sessionName, thresholdMultiplier);

            var result = new
            {
                serverName,
                sessionName,
                thresholdMultiplier,
                anomalyCount = anomalies.Count,
                anomalies
            };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatAnomaliesMarkdown(serverName, sessionName, thresholdMultiplier, anomalies);
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ErrorSanitizer.Sanitize(ex, _logger)
            }, JsonOptions.Default);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Tool 6: Get Query Trend
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get performance trend data for a specific query fingerprint over time.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_get_query_trend")]
    [Description("""
        Get performance trend data for a specific query fingerprint over time.

        Returns time-series data showing how a query's performance has changed across captures.
        Uses historical observation data stored in memory.
        Useful for tracking whether a query is getting slower over time.
        """)]
    public async Task<string> GetQueryTrend(
        [Description("The query fingerprint hash to analyze")] string queryFingerprint,
        [Description("Maximum number of data points to return")] int bucketCount = 20,
        [Description("Output format: Json or Markdown")] string responseFormat = "Json")
    {
        try
        {
            var connectionString = ConnectionStringResolver.Resolve();
            var serverName = MemoryService.ExtractServerName(connectionString);

            var trendPoints = await _trendAnalysisService.GetQueryTrendAsync(
                serverName, queryFingerprint, bucketCount);

            var result = new
            {
                serverName,
                queryFingerprint,
                bucketCount,
                dataPoints = trendPoints.Count,
                trend = trendPoints
            };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatQueryTrendMarkdown(queryFingerprint, trendPoints);
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ErrorSanitizer.Sanitize(ex, _logger)
            }, JsonOptions.Default);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, out var dt) ? dt : null;
    }

    private static string FormatTableStatsMarkdown(int totalEvents, int uniqueTables, int topN, List<TableStats> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stats by Table");
        sb.AppendLine();
        sb.AppendLine($"**Total Events:** {totalEvents} | **Unique Tables:** {uniqueTables} | **Showing Top:** {topN}");
        sb.AppendLine();
        sb.AppendLine("| # | Table | Calls | Total Duration | Avg Duration | Reads | Writes | Operations |");
        sb.AppendLine("|---|-------|-------|---------------|-------------|-------|--------|-----------|");

        var i = 1;
        foreach (var s in stats)
        {
            var ops = string.Join(", ", s.Operations);
            sb.AppendLine($"| {i++} | `{s.TableName}` | {s.CallCount:N0} | {ProfilerService.FormatDuration(s.TotalDurationUs)} | {s.AvgDurationFormatted} | {s.TotalReads:N0} | {s.TotalWrites:N0} | {ops} |");
        }

        return sb.ToString();
    }

    private static string FormatProcedureStatsMarkdown(int totalEvents, int uniqueProcs, int topN, List<ProcedureStats> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stats by Stored Procedure");
        sb.AppendLine();
        sb.AppendLine($"**Total Events:** {totalEvents} | **Unique Procedures:** {uniqueProcs} | **Showing Top:** {topN}");
        sb.AppendLine();
        sb.AppendLine("| # | Procedure | Calls | Total Duration | Avg Duration | Reads | Writes |");
        sb.AppendLine("|---|-----------|-------|---------------|-------------|-------|--------|");

        var i = 1;
        foreach (var s in stats)
        {
            sb.AppendLine($"| {i++} | `{s.ProcedureName}` | {s.CallCount:N0} | {ProfilerService.FormatDuration(s.TotalDurationUs)} | {s.AvgDurationFormatted} | {s.TotalReads:N0} | {s.TotalWrites:N0} |");
        }

        return sb.ToString();
    }

    private static string FormatOperationStatsMarkdown(int totalEvents, int uniqueOps, List<OperationStats> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Stats by Operation Type");
        sb.AppendLine();
        sb.AppendLine($"**Total Events:** {totalEvents} | **Operation Types:** {uniqueOps}");
        sb.AppendLine();
        sb.AppendLine("| # | Operation | Calls | Total Duration | Avg Duration | Reads | Writes |");
        sb.AppendLine("|---|-----------|-------|---------------|-------------|-------|--------|");

        var i = 1;
        foreach (var s in stats)
        {
            sb.AppendLine($"| {i++} | {s.OperationType} | {s.CallCount:N0} | {ProfilerService.FormatDuration(s.TotalDurationUs)} | {s.AvgDurationFormatted} | {s.TotalReads:N0} | {s.TotalWrites:N0} |");
        }

        return sb.ToString();
    }

    private static string FormatComparisonMarkdown(PeriodComparison comparison)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Period Comparison");
        sb.AppendLine();

        sb.AppendLine("## Period 1");
        sb.AppendLine($"- **Range:** {comparison.Period1.Start:yyyy-MM-dd HH:mm:ss} — {comparison.Period1.End:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Events:** {comparison.Period1.TotalEvents:N0} | **Unique Queries:** {comparison.Period1.UniqueQueries:N0}");
        sb.AppendLine($"- **Total Duration:** {ProfilerService.FormatDuration(comparison.Period1.TotalDurationUs)} | **Avg:** {ProfilerService.FormatDuration((long)comparison.Period1.AvgDurationUs)}");
        sb.AppendLine($"- **Reads:** {comparison.Period1.TotalReads:N0}");
        sb.AppendLine();

        sb.AppendLine("## Period 2");
        sb.AppendLine($"- **Range:** {comparison.Period2.Start:yyyy-MM-dd HH:mm:ss} — {comparison.Period2.End:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Events:** {comparison.Period2.TotalEvents:N0} | **Unique Queries:** {comparison.Period2.UniqueQueries:N0}");
        sb.AppendLine($"- **Total Duration:** {ProfilerService.FormatDuration(comparison.Period2.TotalDurationUs)} | **Avg:** {ProfilerService.FormatDuration((long)comparison.Period2.AvgDurationUs)}");
        sb.AppendLine($"- **Reads:** {comparison.Period2.TotalReads:N0}");
        sb.AppendLine();

        if (comparison.SlowerQueries.Count > 0)
        {
            sb.AppendLine($"## Slower Queries ({comparison.SlowerQueries.Count})");
            sb.AppendLine();
            sb.AppendLine("| # | Fingerprint | Period 1 Avg | Period 2 Avg | Change |");
            sb.AppendLine("|---|-------------|-------------|-------------|--------|");
            var i = 1;
            foreach (var d in comparison.SlowerQueries)
            {
                sb.AppendLine($"| {i++} | `{(d.QueryFingerprint.Length > 16 ? d.QueryFingerprint[..16] : d.QueryFingerprint)}` | {ProfilerService.FormatDuration((long)d.Period1AvgDurationUs)} | {ProfilerService.FormatDuration((long)d.Period2AvgDurationUs)} | +{d.ChangePercent:N1}% |");
            }
            sb.AppendLine();
        }

        if (comparison.FasterQueries.Count > 0)
        {
            sb.AppendLine($"## Faster Queries ({comparison.FasterQueries.Count})");
            sb.AppendLine();
            sb.AppendLine("| # | Fingerprint | Period 1 Avg | Period 2 Avg | Change |");
            sb.AppendLine("|---|-------------|-------------|-------------|--------|");
            var i = 1;
            foreach (var d in comparison.FasterQueries)
            {
                sb.AppendLine($"| {i++} | `{(d.QueryFingerprint.Length > 16 ? d.QueryFingerprint[..16] : d.QueryFingerprint)}` | {ProfilerService.FormatDuration((long)d.Period1AvgDurationUs)} | {ProfilerService.FormatDuration((long)d.Period2AvgDurationUs)} | {d.ChangePercent:N1}% |");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"**New queries in Period 2:** {comparison.NewQueryFingerprints.Count}");
        sb.AppendLine($"**Queries gone in Period 2:** {comparison.DisappearedQueryFingerprints.Count}");

        return sb.ToString();
    }

    private static string FormatAnomaliesMarkdown(string serverName, string sessionName, double threshold, List<AnomalyResult> anomalies)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Anomaly Detection Results");
        sb.AppendLine();
        sb.AppendLine($"**Server:** {serverName} | **Session:** {sessionName} | **Threshold:** {threshold:N1}x historical average");
        sb.AppendLine($"**Anomalies Found:** {anomalies.Count}");
        sb.AppendLine();

        if (anomalies.Count == 0)
        {
            sb.AppendLine("No anomalies detected. All queries are performing within expected ranges.");
            return sb.ToString();
        }

        sb.AppendLine("| # | Severity | Metric | Fingerprint | Current | Historical Avg | Deviation |");
        sb.AppendLine("|---|----------|--------|-------------|---------|---------------|-----------|");

        var i = 1;
        foreach (var a in anomalies)
        {
            var current = a.Metric == "Duration"
                ? ProfilerService.FormatDuration((long)a.CurrentValue)
                : $"{a.CurrentValue:N0} reads";
            var historical = a.Metric == "Duration"
                ? ProfilerService.FormatDuration((long)a.HistoricalAvg)
                : $"{a.HistoricalAvg:N0} reads";
            var fp = a.QueryFingerprint.Length > 16 ? a.QueryFingerprint[..16] : a.QueryFingerprint;
            sb.AppendLine($"| {i++} | **{a.Severity}** | {a.Metric} | `{fp}` | {current} | {historical} | {a.DeviationFactor:N1}x |");
        }

        return sb.ToString();
    }

    private static string FormatQueryTrendMarkdown(string queryFingerprint, List<QueryTrendPoint> trend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Query Trend");
        sb.AppendLine();
        sb.AppendLine($"**Fingerprint:** `{queryFingerprint}`");
        sb.AppendLine($"**Data Points:** {trend.Count}");
        sb.AppendLine();

        if (trend.Count == 0)
        {
            sb.AppendLine("No historical data found for this fingerprint. Ensure memory is enabled and captures have been taken.");
            return sb.ToString();
        }

        sb.AppendLine("| Time | Executions | Avg Duration | Avg Reads | Avg CPU |");
        sb.AppendLine("|------|-----------|-------------|----------|---------|");

        foreach (var p in trend)
        {
            sb.AppendLine($"| {p.TimeBucket:yyyy-MM-dd HH:mm} | {p.ExecutionCount:N0} | {p.AvgDurationFormatted} | {p.AvgReads:N0} | {ProfilerService.FormatDuration(p.AvgCpuUs)} |");
        }

        return sb.ToString();
    }
}
