using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Tools;

/// <summary>
/// MCP Tools for querying and managing the persistent memory system.
/// Memory stores past profiling captures and per-query performance history.
/// </summary>
[McpServerToolType]
public class MemoryTools
{
    private readonly IMemoryService _memoryService;

    public MemoryTools(IMemoryService memoryService)
    {
        _memoryService = memoryService;
    }

    [McpServerTool(Name = "sqlsentinel_memory_list")]
    [Description("""
        List past profiling captures stored in memory.

        Returns summaries of previous profiling sessions that were automatically saved
        when you retrieved events, stats, or ran health checks.

        Use this to discover what data is available from past operations without re-running them.
        Memory persists across server restarts and conversation compaction.

        Filter by server name, session name, tag, or time range.
        """)]
    public async Task<string> ListCaptures(
        [Description("Filter to specific SQL Server instance")] string? serverName = null,
        [Description("Filter to specific session name")] string? sessionName = null,
        [Description("Filter by tag (e.g., 'regression', 'deploy_v3')")] string? tag = null,
        [Description("Only show captures after this date (ISO format)")] string? since = null,
        [Description("Max results to return (default 20)")] int limit = 20,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            DateTime? sinceDate = null;
            if (!string.IsNullOrWhiteSpace(since) && DateTime.TryParse(since, out var parsed))
                sinceDate = parsed;

            var captures = await _memoryService.GetCapturesAsync(serverName, sessionName, tag, sinceDate, limit);

            if (captures.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No captures found in memory.",
                    suggestion = "Memory is auto-populated when you call get_events, get_stats, stream_events, or health_check."
                }, JsonOptions.Default);
            }

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
                return FormatCaptureListMarkdown(captures);

            return JsonSerializer.Serialize(new { totalCount = captures.Count, captures }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_memory_get_capture")]
    [Description("""
        Get full details of a specific past capture by its ID.

        Returns the complete capture summary including top queries by duration,
        insights, diagnostic findings, and context from that profiling session.

        Use capture IDs from sqlsentinel_memory_list.
        """)]
    public async Task<string> GetCapture(
        [Description("Capture ID from sqlsentinel_memory_list")] string captureId,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var capture = await _memoryService.GetCaptureByIdAsync(captureId);

            if (capture == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Capture '{captureId}' not found.",
                    suggestion = "Use sqlsentinel_memory_list to see available captures."
                }, JsonOptions.Default);
            }

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
                return FormatCaptureDetailMarkdown(capture);

            return JsonSerializer.Serialize(capture, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_memory_query_history")]
    [Description("""
        Get the performance history of a specific query fingerprint across all captures.

        Shows how a query's performance has changed over time: execution counts,
        average/max duration, reads, and CPU usage across multiple profiling sessions.

        Use this to detect performance regressions or improvements.
        Get query fingerprints from get_events, get_stats, or memory_search_queries.
        """)]
    public async Task<string> QueryHistory(
        [Description("SQL Server connection string (used to identify the server)")] string connectionString,
        [Description("Query fingerprint (SHA256 prefix from profiling results)")] string queryFingerprint,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var serverName = MemoryService.ExtractServerName(connectionString);
            var history = await _memoryService.GetQueryHistoryAsync(serverName, queryFingerprint);

            if (history == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"No history found for fingerprint '{queryFingerprint}' on server '{serverName}'.",
                    suggestion = "Use sqlsentinel_memory_search_queries to find queries, or run get_events first to populate memory."
                }, JsonOptions.Default);
            }

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
                return FormatQueryHistoryMarkdown(history);

            return JsonSerializer.Serialize(history, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_memory_search_queries")]
    [Description("""
        Search through remembered queries across all past captures.

        Find queries by SQL text content, database, minimum execution count,
        or minimum average duration. Returns query fingerprints with their
        cumulative statistics and performance history.

        Results are sorted by total duration (heaviest queries first).
        """)]
    public async Task<string> SearchQueries(
        [Description("SQL Server connection string (used to identify the server)")] string connectionString,
        [Description("Search for queries containing this text (e.g., table name, stored procedure)")] string? sqlContains = null,
        [Description("Filter to queries that ran in this database")] string? database = null,
        [Description("Minimum total executions across all captures")] int? minExecutions = null,
        [Description("Minimum average duration in milliseconds")] int? minAvgDurationMs = null,
        [Description("Max results to return (default 20)")] int limit = 20,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var serverName = MemoryService.ExtractServerName(connectionString);
            long? minAvgUs = minAvgDurationMs.HasValue ? minAvgDurationMs.Value * 1000L : null;

            var queries = await _memoryService.SearchQueriesAsync(
                serverName, sqlContains, database, minExecutions, minAvgUs, limit);

            if (queries.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No matching queries found in memory.",
                    suggestion = "Run get_events or get_stats first to populate memory, then search again."
                }, JsonOptions.Default);
            }

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
                return FormatQuerySearchMarkdown(queries, sqlContains, database);

            return JsonSerializer.Serialize(new { totalCount = queries.Count, queries }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_memory_tag")]
    [Description("""
        Add tags or a note to a past capture for easier retrieval later.

        Tags help categorize captures (e.g., 'before_deploy', 'regression_test', 'production_issue').
        Notes provide free-form context about what the capture was for.
        Tagged captures have extended retention (90 days vs 30 days default).
        """)]
    public async Task<string> TagCapture(
        [Description("Capture ID to tag")] string captureId,
        [Description("Comma-separated tags to add (e.g., 'before_deploy,baseline')")] string tags,
        [Description("Free-form note about this capture")] string? note = null)
    {
        try
        {
            var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            if (tagList.Count == 0)
            {
                return JsonSerializer.Serialize(new { success = false, error = "No tags provided." }, JsonOptions.Default);
            }

            var found = await _memoryService.TagCaptureAsync(captureId, tagList, note);

            if (!found)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Capture '{captureId}' not found.",
                    suggestion = "Use sqlsentinel_memory_list to see available captures."
                }, JsonOptions.Default);
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                message = $"Added {tagList.Count} tag(s) to capture {captureId}",
                tags = tagList,
                note
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_memory_stats")]
    [Description("""
        Show memory system statistics: total captures, query count, disk usage, date range.

        Useful for understanding what historical data is available and managing storage.
        """)]
    public async Task<string> GetMemoryStats(
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var stats = await _memoryService.GetMemoryStatsAsync();

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
                return FormatStatsMarkdown(stats);

            return JsonSerializer.Serialize(stats, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_memory_purge")]
    [Description("""
        Delete memory data for a specific server, or clean up expired memories.

        Use 'expired' as serverName to only clean up expired captures.
        Use a specific server name to remove all captures and query history for that server.
        """)]
    public async Task<string> PurgeMemory(
        [Description("Server name to purge, or 'expired' to clean up old data")] string serverName)
    {
        try
        {
            await _memoryService.PurgeServerMemoryAsync(serverName);

            var message = serverName.Equals("expired", StringComparison.OrdinalIgnoreCase)
                ? "Cleaned up expired memory entries."
                : $"Purged all memory for server '{serverName}'.";

            return JsonSerializer.Serialize(new { success = true, message }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, JsonOptions.Default);
        }
    }

    // ── Markdown Formatters ──────────────────────────────────────

    private static string FormatCaptureListMarkdown(List<CaptureMemory> captures)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Memory: Past Captures");
        sb.AppendLine();
        sb.AppendLine($"**{captures.Count} capture(s) found**");
        sb.AppendLine();

        foreach (var c in captures)
        {
            var tags = c.Tags.Count > 0 ? $" [{string.Join(", ", c.Tags)}]" : "";
            sb.AppendLine($"## {c.Id}{tags}");
            sb.AppendLine();
            sb.AppendLine($"- **Server:** {c.ServerName} | **Session:** {c.SessionName}");
            sb.AppendLine($"- **Captured:** {c.CapturedAt:yyyy-MM-dd HH:mm:ss} UTC");
            if (c.TimeRangeStart.HasValue && c.TimeRangeEnd.HasValue)
                sb.AppendLine($"- **Time Range:** {c.TimeRangeStart:HH:mm:ss} → {c.TimeRangeEnd:HH:mm:ss}");
            sb.AppendLine($"- **Events:** {c.TotalEvents} | **Unique Queries:** {c.UniqueQueries}");
            sb.AppendLine($"- **Databases:** {string.Join(", ", c.DatabasesObserved)}");
            if (c.DeadlockCount > 0) sb.AppendLine($"- **Deadlocks:** {c.DeadlockCount}");
            if (c.BlockingEventCount > 0) sb.AppendLine($"- **Blocking Events:** {c.BlockingEventCount}");
            if (c.Insights.Count > 0) sb.AppendLine($"- **Insights:** {c.Insights.Count}");
            if (c.AgentNote != null) sb.AppendLine($"- **Note:** {c.AgentNote}");
            sb.AppendLine();
        }

        sb.AppendLine("*Use `sqlsentinel_memory_get_capture` with a capture ID for full details.*");
        return sb.ToString();
    }

    private static string FormatCaptureDetailMarkdown(CaptureMemory c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Capture: {c.Id}");
        sb.AppendLine();
        sb.AppendLine($"- **Server:** {c.ServerName}");
        sb.AppendLine($"- **Session:** {c.SessionName}");
        sb.AppendLine($"- **Captured:** {c.CapturedAt:yyyy-MM-dd HH:mm:ss} UTC");
        if (c.TimeRangeStart.HasValue && c.TimeRangeEnd.HasValue)
            sb.AppendLine($"- **Time Range:** {c.TimeRangeStart:yyyy-MM-dd HH:mm:ss} → {c.TimeRangeEnd:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **State:** {c.State} | **Expires:** {c.ExpiresAt:yyyy-MM-dd}");
        if (c.Tags.Count > 0) sb.AppendLine($"- **Tags:** {string.Join(", ", c.Tags)}");
        if (c.AgentNote != null) sb.AppendLine($"- **Note:** {c.AgentNote}");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Total Events | {c.TotalEvents:N0} |");
        sb.AppendLine($"| Unique Queries | {c.UniqueQueries:N0} |");
        sb.AppendLine($"| Total Duration | {FormatDuration(c.TotalDurationUs)} |");
        sb.AppendLine($"| Total CPU | {FormatDuration(c.TotalCpuUs)} |");
        sb.AppendLine($"| Total Reads | {c.TotalReads:N0} |");
        sb.AppendLine($"| Total Writes | {c.TotalWrites:N0} |");
        sb.AppendLine($"| Deadlocks | {c.DeadlockCount} |");
        sb.AppendLine($"| Blocking Events | {c.BlockingEventCount} |");
        sb.AppendLine();

        sb.AppendLine($"- **Databases:** {string.Join(", ", c.DatabasesObserved)}");
        sb.AppendLine($"- **Applications:** {string.Join(", ", c.ApplicationsObserved)}");
        sb.AppendLine($"- **Logins:** {string.Join(", ", c.LoginsObserved)}");
        sb.AppendLine();

        if (c.Insights.Count > 0)
        {
            sb.AppendLine("## Insights");
            sb.AppendLine();
            foreach (var insight in c.Insights)
            {
                sb.AppendLine($"- **[{insight.Severity.ToUpperInvariant()}]** [{insight.Category}] {insight.Message}");
                if (insight.Detail != null) sb.AppendLine($"  - `{insight.Detail}`");
            }
            sb.AppendLine();
        }

        if (c.TopQueriesByDuration.Count > 0)
        {
            sb.AppendLine("## Top Queries by Duration");
            sb.AppendLine();
            var i = 1;
            foreach (var q in c.TopQueriesByDuration)
            {
                sb.AppendLine($"### {i}. {q.QueryFingerprint[..Math.Min(16, q.QueryFingerprint.Length)]}");
                sb.AppendLine();
                sb.AppendLine($"- **Executions:** {q.ExecutionCount} | **Total:** {FormatDuration(q.TotalDurationUs)} | **Avg:** {FormatDuration((long)q.AvgDurationUs)} | **Max:** {FormatDuration(q.MaxDurationUs)}");
                sb.AppendLine($"- **Reads:** {q.TotalReads:N0}");
                sb.AppendLine($"- **SQL:** `{q.SqlPreview}`");
                sb.AppendLine();
                i++;
            }
        }

        sb.AppendLine("*Use `sqlsentinel_memory_query_history` with a fingerprint to see performance trends.*");
        return sb.ToString();
    }

    private static string FormatQueryHistoryMarkdown(QueryMemory q)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Query History: {q.QueryFingerprint[..Math.Min(16, q.QueryFingerprint.Length)]}");
        sb.AppendLine();
        sb.AppendLine($"- **Server:** {q.ServerName}");
        sb.AppendLine($"- **First Seen:** {q.FirstSeenAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Last Seen:** {q.LastSeenAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **Captures:** {q.CaptureCount}");
        sb.AppendLine($"- **Databases:** {string.Join(", ", q.DatabasesSeen)}");
        sb.AppendLine($"- **Applications:** {string.Join(", ", q.ApplicationsSeen)}");
        sb.AppendLine();

        sb.AppendLine("## Cumulative Stats");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Total Executions | {q.TotalExecutions:N0} |");
        sb.AppendLine($"| Avg Duration | {FormatDuration((long)q.AvgDurationUs)} |");
        sb.AppendLine($"| Max Duration | {FormatDuration(q.MaxDurationUs)} |");
        sb.AppendLine($"| Min Duration | {FormatDuration(q.MinDurationUs)} |");
        sb.AppendLine($"| Total CPU | {FormatDuration(q.TotalCpuUs)} |");
        sb.AppendLine($"| Total Reads | {q.TotalReads:N0} |");
        sb.AppendLine($"| Total Writes | {q.TotalWrites:N0} |");
        sb.AppendLine();

        sb.AppendLine("## SQL");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine(q.SampleSql);
        sb.AppendLine("```");
        sb.AppendLine();

        if (q.Observations.Count > 0)
        {
            sb.AppendLine("## Performance Over Time");
            sb.AppendLine();
            sb.AppendLine("| Date | Executions | Avg Duration | Max Duration | Avg Reads | Avg CPU |");
            sb.AppendLine("|------|-----------|-------------|-------------|-----------|---------|");

            foreach (var obs in q.Observations.OrderByDescending(o => o.ObservedAt))
            {
                sb.AppendLine($"| {obs.ObservedAt:yyyy-MM-dd HH:mm} | {obs.ExecutionCount} | {FormatDuration(obs.AvgDurationUs)} | {FormatDuration(obs.MaxDurationUs)} | {obs.AvgReads:N0} | {FormatDuration(obs.AvgCpuUs)} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatQuerySearchMarkdown(List<QueryMemory> queries, string? sqlContains, string? database)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Memory: Query Search Results");
        sb.AppendLine();
        var filterParts = new List<string>();
        if (sqlContains != null) filterParts.Add($"SQL contains '{sqlContains}'");
        if (database != null) filterParts.Add($"database = '{database}'");
        if (filterParts.Count > 0) sb.AppendLine($"**Filters:** {string.Join(", ", filterParts)}");
        sb.AppendLine($"**{queries.Count} query fingerprint(s) found**");
        sb.AppendLine();

        var i = 1;
        foreach (var q in queries)
        {
            sb.AppendLine($"## {i}. {q.QueryFingerprint[..Math.Min(16, q.QueryFingerprint.Length)]}");
            sb.AppendLine();
            sb.AppendLine($"- **Executions:** {q.TotalExecutions:N0} across {q.CaptureCount} captures");
            sb.AppendLine($"- **Avg Duration:** {FormatDuration((long)q.AvgDurationUs)} | **Max:** {FormatDuration(q.MaxDurationUs)}");
            sb.AppendLine($"- **Total Reads:** {q.TotalReads:N0} | **Total CPU:** {FormatDuration(q.TotalCpuUs)}");
            sb.AppendLine($"- **Databases:** {string.Join(", ", q.DatabasesSeen)}");
            sb.AppendLine($"- **SQL:** `{(q.SampleSql.Length > 100 ? q.SampleSql[..100] + "..." : q.SampleSql)}`");
            sb.AppendLine();
            i++;
        }

        sb.AppendLine("*Use `sqlsentinel_memory_query_history` with a fingerprint for detailed performance trends.*");
        return sb.ToString();
    }

    private static string FormatStatsMarkdown(MemoryStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Memory System Statistics");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Total Captures | {stats.TotalCaptures} |");
        sb.AppendLine($"| Total Query Fingerprints | {stats.TotalQueryFingerprints} |");
        sb.AppendLine($"| Disk Usage | {FormatBytes(stats.DiskUsageBytes)} |");
        if (stats.OldestCapture.HasValue)
            sb.AppendLine($"| Oldest Capture | {stats.OldestCapture:yyyy-MM-dd HH:mm} |");
        if (stats.NewestCapture.HasValue)
            sb.AppendLine($"| Newest Capture | {stats.NewestCapture:yyyy-MM-dd HH:mm} |");
        sb.AppendLine();

        if (stats.CapturesPerServer.Count > 0)
        {
            sb.AppendLine("## Captures per Server");
            sb.AppendLine();
            foreach (var (server, count) in stats.CapturesPerServer)
            {
                sb.AppendLine($"- **{server}**: {count} captures");
            }
        }

        return sb.ToString();
    }

    // ── Utility ──────────────────────────────────────────────────

    private static string FormatDuration(long microseconds)
    {
        if (microseconds < 1000) return $"{microseconds}µs";
        if (microseconds < 1_000_000) return $"{microseconds / 1000.0:F1}ms";
        return $"{microseconds / 1_000_000.0:F2}s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
