using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Tools;

/// <summary>
/// MCP Tools for advanced diagnostics: deadlocks, blocking, wait stats, health check.
/// </summary>
[McpServerToolType]
public class DiagnosticTools
{
    private readonly IProfilerService _profilerService;
    private readonly IWaitStatsService _waitStatsService;
    private readonly SessionConfigStore _configStore;

    public DiagnosticTools(
        IProfilerService profilerService,
        IWaitStatsService waitStatsService,
        SessionConfigStore configStore)
    {
        _profilerService = profilerService;
        _waitStatsService = waitStatsService;
        _configStore = configStore;
    }

    [McpServerTool(Name = "sqlsentinel_get_deadlocks")]
    [Description("""
        Retrieve deadlock events captured by a profiling session.

        Sessions must include the Deadlock event type (maps to sqlserver.xml_deadlock_report).
        Returns structured deadlock information: victim process, all involved SPIDs,
        wait resources, lock modes, and SQL text per process.
        """)]
    public async Task<string> GetDeadlocks(
        [Description("Name of the profiling session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var deadlocks = await _profilerService.GetDeadlocksAsync(connectionString, sessionName);

            if (deadlocks.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No deadlocks found in this session.",
                    suggestion = "Ensure the session was created with EventType Deadlock included."
                }, JsonOptions.Default);
            }

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatDeadlocksMarkdown(deadlocks);
            }

            return JsonSerializer.Serialize(new
            {
                count = deadlocks.Count,
                deadlocks = deadlocks.Select(d => new
                {
                    d.EventTimestamp,
                    d.VictimSpid,
                    processes = d.Processes.Select(p => new
                    {
                        p.Spid,
                        p.IsVictim,
                        p.LoginName,
                        p.DatabaseName,
                        p.WaitResource,
                        p.LockMode,
                        p.WaitTime,
                        sqlText = p.SqlText?.Length > 500 ? p.SqlText[..500] + "..." : p.SqlText
                    })
                })
            }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                suggestion = "Ensure session exists and includes Deadlock event type."
            }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_get_blocking")]
    [Description("""
        Retrieve blocked process events captured by a profiling session.

        PREREQUISITE: 'blocked process threshold' must be configured on the server:
          EXEC sp_configure 'blocked process threshold', 5;  -- triggers after 5 seconds
          RECONFIGURE;

        Sessions must include the BlockedProcess event type.
        Returns blocked and blocking SPID pairs with wait resources and SQL text.
        """)]
    public async Task<string> GetBlocking(
        [Description("Name of the profiling session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var blockingEvents = await _profilerService.GetBlockingEventsAsync(connectionString, sessionName);

            if (blockingEvents.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No blocking events found in this session.",
                    suggestion = "Ensure: 1) Session includes BlockedProcess event type, 2) 'blocked process threshold' is configured on the server (sp_configure 'blocked process threshold', 5)."
                }, JsonOptions.Default);
            }

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatBlockingMarkdown(blockingEvents);
            }

            return JsonSerializer.Serialize(new
            {
                count = blockingEvents.Count,
                events = blockingEvents.Select(b => new
                {
                    b.EventTimestamp,
                    blocked = new
                    {
                        b.BlockedProcess.Spid,
                        b.BlockedProcess.WaitResource,
                        b.BlockedProcess.WaitTimeMs,
                        b.BlockedProcess.LoginName,
                        b.BlockedProcess.DatabaseName,
                        sqlText = b.BlockedProcess.SqlText?.Length > 500 ? b.BlockedProcess.SqlText[..500] + "..." : b.BlockedProcess.SqlText
                    },
                    blocking = new
                    {
                        b.BlockingProcess.Spid,
                        b.BlockingProcess.LoginName,
                        b.BlockingProcess.DatabaseName,
                        sqlText = b.BlockingProcess.SqlText?.Length > 500 ? b.BlockingProcess.SqlText[..500] + "..." : b.BlockingProcess.SqlText
                    }
                })
            }, JsonOptions.Default);
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

    [McpServerTool(Name = "sqlsentinel_get_wait_stats")]
    [Description("""
        Query sys.dm_os_wait_stats to show what SQL Server is waiting on.

        This does NOT require a profiling session - it queries the server directly.
        Shows cumulative wait statistics since the last SQL Server restart or manual reset.
        Useful for identifying systemic bottlenecks.

        Waits are categorized as: CPU, I/O, Lock, Memory, Network, Latch, Preemptive, Other.
        Common benign waits (SLEEP_*, BROKER_*, background tasks) are excluded.
        """)]
    public async Task<string> GetWaitStats(
        [Description("SQL Server connection string")] string connectionString,
        [Description("Number of top wait types to return")] int topN = 20,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var waitStats = await _waitStatsService.GetWaitStatsAsync(connectionString, topN);

            if (waitStats.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    message = "No significant wait statistics found."
                }, JsonOptions.Default);
            }

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatWaitStatsMarkdown(waitStats);
            }

            return JsonSerializer.Serialize(new
            {
                count = waitStats.Count,
                waitStats
            }, JsonOptions.Default);
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

    [McpServerTool(Name = "sqlsentinel_health_check")]
    [Description("""
        Single diagnostic command that produces a comprehensive server health report.

        Pulls together:
        - Top 5 slowest queries (requires an active profiling session)
        - Recent deadlocks (if session includes Deadlock event type)
        - Recent blocking events (if session includes BlockedProcess event type)
        - Top 10 wait statistics from sys.dm_os_wait_stats
        - Currently active blocking chains from sys.dm_exec_requests
        - AI-ready insights with severity and recommendations

        For sessionName: pass an active or stopped session to include query/event analysis.
        Pass empty string to skip query analysis and only return live DMV data.
        """)]
    public async Task<string> HealthCheck(
        [Description("SQL Server connection string")] string connectionString,
        [Description("Profiling session to analyze (empty string to skip)")] string sessionName = "",
        [Description("Threshold for slow query flagging in ms")] int slowQueryThresholdMs = 1000,
        [Description("Output format: Json or Markdown")] string responseFormat = "Markdown")
    {
        try
        {
            var insights = new List<HealthInsight>();
            var result = new HealthCheckResult();

            // Always fetch: wait stats and active blocking (no session needed)
            var waitStatsTask = _waitStatsService.GetWaitStatsAsync(connectionString, 10);
            var connectionInfoTask = _profilerService.GetConnectionInfoAsync(connectionString, "blocking");

            // Session-dependent fetches
            Task<List<ProfilerEvent>>? eventsTask = null;
            Task<List<DeadlockEvent>>? deadlocksTask = null;
            Task<List<BlockingEvent>>? blockingTask = null;

            if (!string.IsNullOrWhiteSpace(sessionName))
            {
                var config = _configStore.Get(sessionName);
                var excludePatterns = config?.ExcludePatterns ?? NoisePatterns.Default;

                eventsTask = _profilerService.GetEventsAsync(connectionString, sessionName, new EventFilters(), excludePatterns);
                deadlocksTask = _profilerService.GetDeadlocksAsync(connectionString, sessionName);
                blockingTask = _profilerService.GetBlockingEventsAsync(connectionString, sessionName);
            }

            // Await all
            var waitStats = await waitStatsTask;
            var connectionInfo = await connectionInfoTask;

            var activeBlocking = connectionInfo.TryGetValue("activeBlocking", out var blockingObj)
                ? (List<ActiveBlockingInfo>)blockingObj
                : [];

            var slowestQueries = new List<QueryStats>();
            var deadlocks = new List<DeadlockEvent>();
            var blockingEvents = new List<BlockingEvent>();

            if (eventsTask != null)
            {
                var events = await eventsTask;
                // Compute top 5 by total duration using fingerprint grouping
                slowestQueries = events
                    .GroupBy(e => e.QueryFingerprint)
                    .Select(g => new QueryStats
                    {
                        Key = g.Key,
                        Count = g.Count(),
                        TotalDurationUs = g.Sum(e => e.DurationUs),
                        TotalCpuUs = g.Sum(e => e.CpuTimeUs),
                        TotalReads = g.Sum(e => e.LogicalReads),
                        TotalWrites = g.Sum(e => e.Writes),
                        MaxDurationUs = g.Max(e => e.DurationUs),
                        AvgDurationUs = g.Average(e => (double)e.DurationUs),
                        AvgDurationFormatted = ProfilerService.FormatDuration((long)g.Average(e => (double)e.DurationUs)),
                        MaxDurationFormatted = ProfilerService.FormatDuration(g.Max(e => e.DurationUs)),
                        TotalDurationFormatted = ProfilerService.FormatDuration(g.Sum(e => e.DurationUs)),
                        SampleSql = g.First().SqlText.Length > 200 ? g.First().SqlText[..200] : g.First().SqlText
                    })
                    .OrderByDescending(s => s.TotalDurationUs)
                    .Take(5)
                    .ToList();

                // Generate query insights
                foreach (var stat in slowestQueries)
                {
                    if (stat.AvgDurationUs > slowQueryThresholdMs * 1000L)
                    {
                        insights.Add(new HealthInsight
                        {
                            Severity = "Warning",
                            Category = "Performance",
                            Message = $"Slow query: avg {stat.AvgDurationFormatted}, max {stat.MaxDurationFormatted}",
                            Detail = stat.SampleSql
                        });
                    }

                    if (stat.Count > 100)
                    {
                        insights.Add(new HealthInsight
                        {
                            Severity = "Warning",
                            Category = "Pattern",
                            Message = $"High-frequency query: {stat.Count} executions — potential N+1 pattern",
                            Detail = stat.SampleSql
                        });
                    }
                }
            }

            if (deadlocksTask != null)
            {
                deadlocks = await deadlocksTask;
                if (deadlocks.Count > 0)
                {
                    insights.Add(new HealthInsight
                    {
                        Severity = "Critical",
                        Category = "Deadlock",
                        Message = $"{deadlocks.Count} deadlock(s) detected",
                        Detail = $"Most recent: {deadlocks.Last().EventTimestamp:yyyy-MM-dd HH:mm:ss}"
                    });
                }
            }

            if (blockingTask != null)
            {
                blockingEvents = await blockingTask;
                if (blockingEvents.Count > 0)
                {
                    insights.Add(new HealthInsight
                    {
                        Severity = "Warning",
                        Category = "Blocking",
                        Message = $"{blockingEvents.Count} blocking event(s) captured",
                        Detail = $"Most recent: {blockingEvents.Last().EventTimestamp:yyyy-MM-dd HH:mm:ss}"
                    });
                }
            }

            if (activeBlocking.Count > 0)
            {
                insights.Add(new HealthInsight
                {
                    Severity = "Critical",
                    Category = "Blocking",
                    Message = $"{activeBlocking.Count} session(s) currently blocked",
                    Detail = string.Join(", ", activeBlocking.Select(b => $"SPID {b.BlockedSpid} blocked by SPID {b.BlockingSpid}"))
                });
            }

            // Wait stat insights
            var lockWaits = waitStats.Where(w => w.WaitCategory == "Lock").Sum(w => w.WaitTimeMs);
            if (lockWaits > 60_000)
            {
                insights.Add(new HealthInsight
                {
                    Severity = "Warning",
                    Category = "Performance",
                    Message = $"Significant lock wait time: {ProfilerService.FormatMilliseconds(lockWaits)}",
                    Detail = "High lock waits may indicate contention issues"
                });
            }

            result = result with
            {
                SlowestQueries = slowestQueries,
                RecentDeadlocks = deadlocks,
                RecentBlocking = blockingEvents,
                TopWaitStats = waitStats,
                ActiveBlocking = activeBlocking,
                Insights = insights
            };

            if (responseFormat.Equals("Markdown", StringComparison.OrdinalIgnoreCase))
            {
                return FormatHealthCheckMarkdown(result);
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

    // ──────────────────────────────────────────────
    // Markdown formatters
    // ──────────────────────────────────────────────

    private static string FormatDeadlocksMarkdown(List<DeadlockEvent> deadlocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Deadlock Events");
        sb.AppendLine();
        sb.AppendLine($"**Total Deadlocks:** {deadlocks.Count}");
        sb.AppendLine();

        for (var i = 0; i < deadlocks.Count; i++)
        {
            var dl = deadlocks[i];
            sb.AppendLine($"## Deadlock {i + 1} — {dl.EventTimestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"**Victim SPID:** {dl.VictimSpid}");
            sb.AppendLine();

            foreach (var proc in dl.Processes)
            {
                var victimTag = proc.IsVictim ? " [VICTIM]" : "";
                sb.AppendLine($"### SPID {proc.Spid}{victimTag}");
                sb.AppendLine();
                sb.AppendLine($"- **Login:** {proc.LoginName} | **Host:** {proc.HostName} | **App:** {proc.ApplicationName}");
                sb.AppendLine($"- **Database:** {proc.DatabaseName} | **Wait Resource:** {proc.WaitResource}");
                sb.AppendLine($"- **Lock Mode:** {proc.LockMode} | **Wait Time:** {proc.WaitTime}ms");
                if (!string.IsNullOrEmpty(proc.SqlText))
                {
                    sb.AppendLine();
                    sb.AppendLine("```sql");
                    sb.AppendLine(proc.SqlText.Length > 500 ? proc.SqlText[..500] + "..." : proc.SqlText);
                    sb.AppendLine("```");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string FormatBlockingMarkdown(List<BlockingEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Blocking Events");
        sb.AppendLine();
        sb.AppendLine($"**Total Events:** {events.Count}");
        sb.AppendLine();

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            sb.AppendLine($"## Event {i + 1} — {evt.EventTimestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine($"**Blocked SPID {evt.BlockedProcess.Spid}** by **Blocking SPID {evt.BlockingProcess.Spid}**");
            sb.AppendLine();
            sb.AppendLine($"- **Wait Resource:** {evt.BlockedProcess.WaitResource} | **Wait Time:** {evt.BlockedProcess.WaitTimeMs}ms");
            sb.AppendLine($"- **Blocked Login:** {evt.BlockedProcess.LoginName} | **DB:** {evt.BlockedProcess.DatabaseName}");
            sb.AppendLine($"- **Blocking Login:** {evt.BlockingProcess.LoginName} | **DB:** {evt.BlockingProcess.DatabaseName}");

            if (!string.IsNullOrEmpty(evt.BlockedProcess.SqlText))
            {
                sb.AppendLine();
                sb.AppendLine("**Blocked SQL:**");
                sb.AppendLine("```sql");
                sb.AppendLine(evt.BlockedProcess.SqlText.Length > 300 ? evt.BlockedProcess.SqlText[..300] + "..." : evt.BlockedProcess.SqlText);
                sb.AppendLine("```");
            }

            if (!string.IsNullOrEmpty(evt.BlockingProcess.SqlText))
            {
                sb.AppendLine();
                sb.AppendLine("**Blocking SQL:**");
                sb.AppendLine("```sql");
                sb.AppendLine(evt.BlockingProcess.SqlText.Length > 300 ? evt.BlockingProcess.SqlText[..300] + "..." : evt.BlockingProcess.SqlText);
                sb.AppendLine("```");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatWaitStatsMarkdown(List<WaitStatEntry> waitStats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Wait Statistics");
        sb.AppendLine();
        sb.AppendLine("| # | Wait Type | Category | Wait Time | Max Wait | Tasks Count |");
        sb.AppendLine("|---|-----------|----------|-----------|----------|-------------|");

        for (var i = 0; i < waitStats.Count; i++)
        {
            var w = waitStats[i];
            sb.AppendLine($"| {i + 1} | {w.WaitType} | {w.WaitCategory} | {w.WaitTimeFormatted} | {w.MaxWaitTimeFormatted} | {w.WaitingTasksCount:N0} |");
        }

        return sb.ToString();
    }

    private static string FormatHealthCheckMarkdown(HealthCheckResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SQL Sentinel Health Check");
        sb.AppendLine();
        sb.AppendLine($"**Checked At:** {result.CheckedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        // Insights (most important — show first)
        if (result.Insights.Count > 0)
        {
            sb.AppendLine("## Insights");
            sb.AppendLine();
            foreach (var insight in result.Insights.OrderByDescending(i => i.Severity == "Critical" ? 2 : i.Severity == "Warning" ? 1 : 0))
            {
                var icon = insight.Severity == "Critical" ? "CRITICAL" : insight.Severity == "Warning" ? "WARNING" : "INFO";
                sb.AppendLine($"- **[{icon}]** [{insight.Category}] {insight.Message}");
                if (!string.IsNullOrEmpty(insight.Detail))
                    sb.AppendLine($"  - {(insight.Detail.Length > 150 ? insight.Detail[..150] + "..." : insight.Detail)}");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Insights");
            sb.AppendLine();
            sb.AppendLine("No issues detected.");
            sb.AppendLine();
        }

        // Active blocking (live)
        if (result.ActiveBlocking.Count > 0)
        {
            sb.AppendLine("## Active Blocking (Live)");
            sb.AppendLine();
            foreach (var b in result.ActiveBlocking)
            {
                sb.AppendLine($"- SPID **{b.BlockedSpid}** blocked by **{b.BlockingSpid}** — Wait: {b.WaitType} ({b.WaitTimeMs}ms) on {b.WaitResource}");
            }
            sb.AppendLine();
        }

        // Deadlocks
        if (result.RecentDeadlocks.Count > 0)
        {
            sb.AppendLine($"## Deadlocks ({result.RecentDeadlocks.Count})");
            sb.AppendLine();
            foreach (var dl in result.RecentDeadlocks.TakeLast(3))
            {
                sb.AppendLine($"- {dl.EventTimestamp:HH:mm:ss} — Victim SPID {dl.VictimSpid}, {dl.Processes.Count} processes involved");
            }
            sb.AppendLine();
        }

        // Blocking events
        if (result.RecentBlocking.Count > 0)
        {
            sb.AppendLine($"## Blocking Events ({result.RecentBlocking.Count})");
            sb.AppendLine();
            foreach (var b in result.RecentBlocking.TakeLast(3))
            {
                sb.AppendLine($"- {b.EventTimestamp:HH:mm:ss} — SPID {b.BlockedProcess.Spid} blocked by {b.BlockingProcess.Spid} for {b.BlockedProcess.WaitTimeMs}ms");
            }
            sb.AppendLine();
        }

        // Top slow queries
        if (result.SlowestQueries.Count > 0)
        {
            sb.AppendLine("## Slowest Queries");
            sb.AppendLine();
            for (var i = 0; i < result.SlowestQueries.Count; i++)
            {
                var q = result.SlowestQueries[i];
                sb.AppendLine($"{i + 1}. **{q.TotalDurationFormatted}** total ({q.Count}x, avg {q.AvgDurationFormatted}) — `{(q.SampleSql.Length > 80 ? q.SampleSql[..80] + "..." : q.SampleSql)}`");
            }
            sb.AppendLine();
        }

        // Wait stats
        if (result.TopWaitStats.Count > 0)
        {
            sb.AppendLine("## Top Wait Stats");
            sb.AppendLine();
            sb.AppendLine("| Wait Type | Category | Wait Time |");
            sb.AppendLine("|-----------|----------|-----------|");
            foreach (var w in result.TopWaitStats.Take(10))
            {
                sb.AppendLine($"| {w.WaitType} | {w.WaitCategory} | {w.WaitTimeFormatted} |");
            }
        }

        return sb.ToString();
    }
}
