using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Tools;

/// <summary>
/// MCP Tools for retrieving and analyzing captured events.
/// </summary>
[McpServerToolType]
public class EventRetrievalTools
{
    private readonly IProfilerService _profilerService;
    private readonly SessionConfigStore _configStore;
    private readonly IQueryFingerprintService _fingerprintService;

    public EventRetrievalTools(
        IProfilerService profilerService, 
        SessionConfigStore configStore,
        IQueryFingerprintService fingerprintService)
    {
        _profilerService = profilerService;
        _configStore = configStore;
        _fingerprintService = fingerprintService;
    }

    /// <summary>
    /// Retrieve captured events from a profiling session with filtering.
    /// </summary>
    [McpServerTool(Name = "sqlprofiler_get_events")]
    [Description("""
        Retrieve captured events from a profiling session with filtering.
        
        Returns query events with timing, resource usage, and metadata.
        Supports filtering by time, database, application, duration, and text content.
        
        When deduplicate=true (default), identical queries are grouped with execution counts.
        """)]
    public async Task<string> GetEvents(
        [Description("Name of the profiling session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString,
        [Description("Filter events after this time (ISO format: '2024-01-15T10:30:00')")] string? startTime = null,
        [Description("Filter events before this time (ISO format)")] string? endTime = null,
        [Description("Filter to specific database")] string? database = null,
        [Description("Filter to specific application")] string? application = null,
        [Description("Filter to specific login")] string? login = null,
        [Description("Filter to queries containing this text")] string? textContains = null,
        [Description("Exclude queries containing this text")] string? textNotContains = null,
        [Description("Minimum duration in milliseconds")] int? minDurationMs = null,
        [Description("Maximum events to return (1-10000)")] int limit = 100,
        [Description("Offset for pagination")] int offset = 0,
        [Description("Sort order: TimestampAsc, TimestampDesc, DurationAsc, DurationDesc, CpuDesc, ReadsDesc")] string sortBy = "TimestampAsc",
        [Description("Group identical queries and show counts")] bool deduplicate = true,
        [Description("Output format: Json or Markdown")] string responseFormat = "Json")
    {
        try
        {
            var config = _configStore.Get(sessionName);
            var excludePatterns = config?.ExcludePatterns ?? NoisePatterns.Default;

            var filters = new EventFilters
            {
                Database = database,
                Application = application,
                Login = login,
                TextContains = textContains,
                TextNotContains = textNotContains,
                MinDurationMs = minDurationMs,
                StartTime = ParseDateTime(startTime),
                EndTime = ParseDateTime(endTime)
            };

            var events = await _profilerService.GetEventsAsync(connectionString, sessionName, filters, excludePatterns);

            // Sort events
            events = SortEvents(events, Enum.Parse<SortOrder>(sortBy, ignoreCase: true));

            // Deduplicate if requested
            if (deduplicate)
            {
                events = DeduplicateEvents(events);
                events = SortEvents(events, Enum.Parse<SortOrder>(sortBy, ignoreCase: true));
            }

            var totalCount = events.Count;
            events = events.Skip(offset).Take(Math.Min(limit, 10000)).ToList();

            var result = new
            {
                totalCount,
                returnedCount = events.Count,
                offset,
                hasMore = totalCount > offset + events.Count,
                deduplicated = deduplicate,
                events
            };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatEventsMarkdown(result.totalCount, result.returnedCount, result.hasMore, result.deduplicated, events);
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                suggestion = "Ensure session exists and is running. Check sqlprofiler_list_sessions."
            }, JsonOptions.Default);
        }
    }

    /// <summary>
    /// Get aggregate statistics from captured events.
    /// </summary>
    [McpServerTool(Name = "sqlprofiler_get_stats")]
    [Description("""
        Get aggregate statistics from captured events.
        
        Provides summary metrics grouped by query fingerprint, database, application, or login.
        Useful for identifying slow queries, high-frequency queries, or resource-intensive operations.
        """)]
    public async Task<string> GetStats(
        [Description("Name of the profiling session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString,
        [Description("Analysis window start (ISO format)")] string? startTime = null,
        [Description("Analysis window end (ISO format)")] string? endTime = null,
        [Description("Group by: QueryFingerprint, Database, Application, Login, or None")] string groupBy = "QueryFingerprint",
        [Description("Return top N results")] int topN = 20,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
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

            // Group events
            var groups = new Dictionary<string, (int count, long totalDuration, long totalCpu, long totalReads, long totalWrites, long maxDuration, string sampleSql)>();

            foreach (var evt in events)
            {
                var key = groupBy.ToLowerInvariant() switch
                {
                    "queryfingerprint" => evt.QueryFingerprint,
                    "database" => evt.DatabaseName ?? "unknown",
                    "application" => evt.ClientAppName ?? "unknown",
                    "login" => evt.LoginName ?? "unknown",
                    _ => "all"
                };

                if (!groups.ContainsKey(key))
                {
                    groups[key] = (0, 0, 0, 0, 0, 0, evt.SqlText.Length > 200 ? evt.SqlText[..200] : evt.SqlText);
                }

                var g = groups[key];
                groups[key] = (
                    g.count + 1,
                    g.totalDuration + evt.DurationUs,
                    g.totalCpu + evt.CpuTimeUs,
                    g.totalReads + evt.LogicalReads,
                    g.totalWrites + evt.Writes,
                    Math.Max(g.maxDuration, evt.DurationUs),
                    g.sampleSql
                );
            }

            var stats = groups
                .Select(g => new QueryStats
                {
                    Key = g.Key,
                    Count = g.Value.count,
                    TotalDurationUs = g.Value.totalDuration,
                    TotalCpuUs = g.Value.totalCpu,
                    TotalReads = g.Value.totalReads,
                    TotalWrites = g.Value.totalWrites,
                    MaxDurationUs = g.Value.maxDuration,
                    AvgDurationUs = g.Value.count > 0 ? g.Value.totalDuration / (double)g.Value.count : 0,
                    AvgDurationFormatted = ProfilerService.FormatDuration((long)(g.Value.count > 0 ? g.Value.totalDuration / g.Value.count : 0)),
                    MaxDurationFormatted = ProfilerService.FormatDuration(g.Value.maxDuration),
                    TotalDurationFormatted = ProfilerService.FormatDuration(g.Value.totalDuration),
                    SampleSql = g.Value.sampleSql
                })
                .OrderByDescending(s => s.TotalDurationUs)
                .Take(topN)
                .ToList();

            var summary = new
            {
                totalEvents = events.Count,
                uniqueGroups = groups.Count,
                timeRange = events.Any(e => e.EventTimestamp.HasValue) ? new
                {
                    start = events.Where(e => e.EventTimestamp.HasValue).Min(e => e.EventTimestamp),
                    end = events.Where(e => e.EventTimestamp.HasValue).Max(e => e.EventTimestamp)
                } : null,
                totals = new
                {
                    durationUs = events.Sum(e => e.DurationUs),
                    cpuUs = events.Sum(e => e.CpuTimeUs),
                    reads = events.Sum(e => e.LogicalReads),
                    writes = events.Sum(e => e.Writes)
                }
            };

            var result = new { summary, groupBy, topN, stats };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatStatsMarkdown(summary, groupBy, topN, stats);
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            }, JsonOptions.Default);
        }
    }

    /// <summary>
    /// Analyze the sequence and timing of queries for a specific operation.
    /// </summary>
    [McpServerTool(Name = "sqlprofiler_analyze_sequence")]
    [Description("""
        Analyze the sequence and timing of queries for a specific operation.
        
        Perfect for understanding what queries run during a specific user action, API call, or business operation.
        Shows queries in execution order with cumulative timing and gaps between queries.
        
        Use correlationId to track queries containing a specific identifier (e.g., order ID, request ID),
        or sessionId to follow a specific database connection (SPID).
        """)]
    public async Task<string> AnalyzeSequence(
        [Description("Name of the profiling session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString,
        [Description("Analysis window start (ISO format)")] string? startTime = null,
        [Description("Analysis window end (ISO format)")] string? endTime = null,
        [Description("Text to search for in queries (e.g., order ID, request ID)")] string? correlationId = null,
        [Description("Specific SPID/session_id to track")] int? sessionId = null,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var config = _configStore.Get(sessionName);
            var excludePatterns = config?.ExcludePatterns ?? NoisePatterns.Default;

            var filters = new EventFilters
            {
                StartTime = ParseDateTime(startTime),
                EndTime = ParseDateTime(endTime),
                TextContains = correlationId
            };

            var events = await _profilerService.GetEventsAsync(connectionString, sessionName, filters, excludePatterns);

            // Filter by session_id if specified
            if (sessionId.HasValue)
            {
                events = events.Where(e => e.SessionId == sessionId.Value).ToList();
            }

            if (events.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No matching events found",
                    suggestion = "Check correlationId spelling, expand time window, or verify session is capturing events"
                }, JsonOptions.Default);
            }

            // Sort by timestamp
            events = events.OrderBy(e => e.EventTimestamp ?? DateTime.MinValue).ToList();

            // Build sequence analysis
            var sequence = new List<SequenceEntry>();
            long cumulativeUs = 0;
            DateTime? prevEnd = null;

            for (var i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                long gapUs = 0;

                if (prevEnd.HasValue && evt.EventTimestamp.HasValue)
                {
                    gapUs = Math.Max(0, (long)(evt.EventTimestamp.Value - prevEnd.Value).TotalMicroseconds);
                }

                cumulativeUs += evt.DurationUs;

                sequence.Add(new SequenceEntry
                {
                    SequenceNumber = i + 1,
                    Timestamp = evt.EventTimestamp,
                    EventType = evt.EventName,
                    Database = evt.DatabaseName,
                    SessionId = evt.SessionId,
                    TransactionId = evt.TransactionId,
                    DurationUs = evt.DurationUs,
                    DurationFormatted = ProfilerService.FormatDuration(evt.DurationUs),
                    GapFromPreviousUs = gapUs,
                    GapFormatted = ProfilerService.FormatDuration(gapUs),
                    CumulativeUs = cumulativeUs,
                    CumulativeFormatted = ProfilerService.FormatDuration(cumulativeUs),
                    SqlText = evt.SqlText.Length > 500 ? evt.SqlText[..500] : evt.SqlText,
                    QueryFingerprint = evt.QueryFingerprint
                });

                if (evt.EventTimestamp.HasValue)
                {
                    prevEnd = evt.EventTimestamp.Value.AddTicks(evt.DurationUs * 10); // Convert µs to ticks
                }
            }

            var patterns = new
            {
                uniqueTransactions = events.Where(e => e.TransactionId.HasValue).Select(e => e.TransactionId).Distinct().Count(),
                uniqueSessions = events.Select(e => e.SessionId).Distinct().Count(),
                databasesTouched = events.Select(e => e.DatabaseName).Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList(),
                repeatedQueries = events.GroupBy(e => e.QueryFingerprint).Count(g => g.Count() > 1)
            };

            var result = new
            {
                totalQueries = sequence.Count,
                totalDurationUs = cumulativeUs,
                totalDurationFormatted = ProfilerService.FormatDuration(cumulativeUs),
                timeRange = new
                {
                    start = sequence.FirstOrDefault()?.Timestamp,
                    end = sequence.LastOrDefault()?.Timestamp
                },
                patterns,
                sequence
            };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatSequenceMarkdown(result.totalQueries, result.totalDurationFormatted, patterns, sequence);
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            }, JsonOptions.Default);
        }
    }

    /// <summary>
    /// Get information about databases, applications, logins, or active sessions.
    /// </summary>
    [McpServerTool(Name = "sqlprofiler_get_connection_info")]
    [Description("""
        Get information about databases, applications, logins, or active sessions.
        
        Useful for understanding what's connected to the server and for configuring session filters.
        """)]
    public async Task<string> GetConnectionInfo(
        [Description("SQL Server connection string")] string connectionString,
        [Description("What to retrieve: databases, applications, logins, sessions, or all")] string infoType = "all")
    {
        try
        {
            var result = await _profilerService.GetConnectionInfoAsync(connectionString, infoType.ToLowerInvariant());
            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message
            }, JsonOptions.Default);
        }
    }

    // Helper methods

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTime.TryParse(value, out var dt))
            return dt;

        return null;
    }

    private static List<ProfilerEvent> SortEvents(List<ProfilerEvent> events, SortOrder sortOrder)
    {
        return sortOrder switch
        {
            SortOrder.TimestampAsc => events.OrderBy(e => e.EventTimestamp ?? DateTime.MinValue).ToList(),
            SortOrder.TimestampDesc => events.OrderByDescending(e => e.EventTimestamp ?? DateTime.MinValue).ToList(),
            SortOrder.DurationAsc => events.OrderBy(e => e.DurationUs).ToList(),
            SortOrder.DurationDesc => events.OrderByDescending(e => e.DurationUs).ToList(),
            SortOrder.CpuDesc => events.OrderByDescending(e => e.CpuTimeUs).ToList(),
            SortOrder.ReadsDesc => events.OrderByDescending(e => e.LogicalReads).ToList(),
            _ => events
        };
    }

    private static List<ProfilerEvent> DeduplicateEvents(List<ProfilerEvent> events)
    {
        var grouped = new Dictionary<string, ProfilerEvent>();

        foreach (var evt in events)
        {
            var key = evt.QueryFingerprint;
            if (grouped.TryGetValue(key, out var existing))
            {
                existing.ExecutionCount++;
                existing.TotalDurationUs += evt.DurationUs;
                existing.TotalCpuUs += evt.CpuTimeUs;
                existing.TotalReads += evt.LogicalReads;
                if (evt.DurationUs > existing.MaxDurationUs)
                    existing.MaxDurationUs = evt.DurationUs;
            }
            else
            {
                var copy = evt with
                {
                    ExecutionCount = 1,
                    TotalDurationUs = evt.DurationUs,
                    TotalCpuUs = evt.CpuTimeUs,
                    TotalReads = evt.LogicalReads,
                    MaxDurationUs = evt.DurationUs
                };
                grouped[key] = copy;
            }
        }

        return grouped.Values.ToList();
    }

    private static string FormatEventsMarkdown(int totalCount, int returnedCount, bool hasMore, bool deduplicated, List<ProfilerEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Query Events");
        sb.AppendLine();
        sb.AppendLine($"**Total:** {totalCount} | **Returned:** {returnedCount} | **Deduplicated:** {deduplicated}");
        sb.AppendLine();

        var i = 1;
        foreach (var evt in events)
        {
            var countStr = evt.ExecutionCount > 1 ? $" (×{evt.ExecutionCount})" : "";
            sb.AppendLine($"## {i}. {evt.EventName}{countStr}");
            sb.AppendLine();
            sb.AppendLine($"- **Time:** {evt.EventTimestamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"- **Duration:** {evt.DurationFormatted} | **CPU:** {ProfilerService.FormatDuration(evt.CpuTimeUs)}");
            sb.AppendLine($"- **Reads:** {evt.LogicalReads:N0} | **Writes:** {evt.Writes:N0}");
            sb.AppendLine($"- **Database:** {evt.DatabaseName} | **App:** {evt.ClientAppName}");
            sb.AppendLine($"- **Session:** {evt.SessionId} | **Login:** {evt.LoginName}");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(evt.SqlText.Length > 1000 ? evt.SqlText[..1000] + "..." : evt.SqlText);
            sb.AppendLine("```");
            sb.AppendLine();
            i++;
        }

        if (hasMore)
        {
            sb.AppendLine($"*{totalCount - returnedCount} more events available. Increase limit or use offset.*");
        }

        return sb.ToString();
    }

    private static string FormatStatsMarkdown(dynamic summary, string groupBy, int topN, List<QueryStats> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Query Statistics");
        sb.AppendLine();
        sb.AppendLine($"**Total Events:** {summary.totalEvents} | **Unique Groups:** {summary.uniqueGroups}");
        sb.AppendLine($"**Total Duration:** {ProfilerService.FormatDuration(summary.totals.durationUs)} | **Total CPU:** {ProfilerService.FormatDuration(summary.totals.cpuUs)}");
        sb.AppendLine($"**Total Reads:** {summary.totals.reads:N0} | **Total Writes:** {summary.totals.writes:N0}");
        sb.AppendLine();
        sb.AppendLine($"## Top {topN} by {groupBy}");
        sb.AppendLine();

        var i = 1;
        foreach (var stat in stats)
        {
            sb.AppendLine($"### {i}. {(stat.Key.Length > 80 ? stat.Key[..80] : stat.Key)}");
            sb.AppendLine();
            sb.AppendLine($"- **Count:** {stat.Count} | **Total:** {stat.TotalDurationFormatted} | **Avg:** {stat.AvgDurationFormatted} | **Max:** {stat.MaxDurationFormatted}");
            sb.AppendLine($"- **Reads:** {stat.TotalReads:N0} | **Writes:** {stat.TotalWrites:N0}");
            sb.AppendLine($"- **Sample:** `{(stat.SampleSql.Length > 100 ? stat.SampleSql[..100] + "..." : stat.SampleSql)}`");
            sb.AppendLine();
            i++;
        }

        return sb.ToString();
    }

    private static string FormatSequenceMarkdown(int totalQueries, string totalDuration, dynamic patterns, List<SequenceEntry> sequence)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Query Sequence Analysis");
        sb.AppendLine();
        sb.AppendLine($"**Total Queries:** {totalQueries} | **Total Duration:** {totalDuration}");
        sb.AppendLine($"**Unique Transactions:** {patterns.uniqueTransactions} | **Sessions:** {patterns.uniqueSessions}");
        sb.AppendLine($"**Databases:** {string.Join(", ", patterns.databasesTouched)}");
        sb.AppendLine($"**Repeated Queries:** {patterns.repeatedQueries}");
        sb.AppendLine();
        sb.AppendLine("## Execution Sequence");
        sb.AppendLine();

        foreach (var entry in sequence)
        {
            sb.AppendLine($"### {entry.SequenceNumber}. {entry.Timestamp:HH:mm:ss.fff}");
            sb.AppendLine();
            sb.AppendLine($"- **Duration:** {entry.DurationFormatted} | **Gap:** +{entry.GapFormatted} | **Cumulative:** {entry.CumulativeFormatted}");
            sb.AppendLine($"- **Session:** {entry.SessionId} | **TxID:** {entry.TransactionId} | **DB:** {entry.Database}");
            sb.AppendLine();
            sb.AppendLine("```sql");
            sb.AppendLine(entry.SqlText);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
