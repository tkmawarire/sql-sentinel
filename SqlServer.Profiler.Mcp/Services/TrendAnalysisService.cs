using SqlServer.Profiler.Mcp.Models;

namespace SqlServer.Profiler.Mcp.Services;

/// <summary>
/// Service for analyzing query performance trends, detecting anomalies, and comparing
/// performance across time periods using historical memory data.
/// </summary>
public interface ITrendAnalysisService
{
    /// <summary>
    /// Compares query performance between two time periods, identifying queries that
    /// became slower, faster, new, or disappeared.
    /// </summary>
    Task<PeriodComparison> ComparePeriodsAsync(
        string connectionString, string sessionName,
        DateTime period1Start, DateTime period1End,
        DateTime period2Start, DateTime period2End,
        CancellationToken ct = default);

    /// <summary>
    /// Detects queries whose current performance deviates significantly from their
    /// historical baseline stored in memory.
    /// </summary>
    Task<List<AnomalyResult>> DetectAnomaliesAsync(
        string serverName, string connectionString, string sessionName,
        double thresholdMultiplier = 2.0,
        CancellationToken ct = default);

    /// <summary>
    /// Returns time-series trend data for a specific query fingerprint from memory observations.
    /// </summary>
    Task<List<QueryTrendPoint>> GetQueryTrendAsync(
        string serverName, string queryFingerprint,
        int bucketCount = 20,
        CancellationToken ct = default);
}

/// <summary>
/// Implements trend analysis using profiler events and the memory service for historical baselines.
/// </summary>
public class TrendAnalysisService : ITrendAnalysisService
{
    private readonly IProfilerService _profilerService;
    private readonly IMemoryService _memoryService;
    private readonly IQueryFingerprintService _fingerprintService;

    /// <summary>
    /// Initializes a new instance of <see cref="TrendAnalysisService"/>.
    /// </summary>
    /// <param name="profilerService">Profiler service for retrieving captured events.</param>
    /// <param name="memoryService">Memory service for accessing historical query data.</param>
    /// <param name="fingerprintService">Fingerprint service for SQL normalization.</param>
    public TrendAnalysisService(
        IProfilerService profilerService,
        IMemoryService memoryService,
        IQueryFingerprintService fingerprintService)
    {
        _profilerService = profilerService;
        _memoryService = memoryService;
        _fingerprintService = fingerprintService;
    }

    /// <inheritdoc/>
    public async Task<PeriodComparison> ComparePeriodsAsync(
        string connectionString, string sessionName,
        DateTime period1Start, DateTime period1End,
        DateTime period2Start, DateTime period2End,
        CancellationToken ct = default)
    {
        // Fetch events for both periods in parallel
        var period1Task = _profilerService.GetEventsAsync(
            connectionString, sessionName,
            new EventFilters { StartTime = period1Start, EndTime = period1End },
            ct: ct);

        var period2Task = _profilerService.GetEventsAsync(
            connectionString, sessionName,
            new EventFilters { StartTime = period2Start, EndTime = period2End },
            ct: ct);

        await Task.WhenAll(period1Task, period2Task);

        var period1Events = period1Task.Result;
        var period2Events = period2Task.Result;

        // Group by fingerprint
        var period1ByFingerprint = GroupByFingerprint(period1Events);
        var period2ByFingerprint = GroupByFingerprint(period2Events);

        // Build period summaries
        var summary1 = BuildPeriodSummary(period1Start, period1End, period1Events, period1ByFingerprint);
        var summary2 = BuildPeriodSummary(period2Start, period2End, period2Events, period2ByFingerprint);

        // Identify query changes
        var slowerQueries = new List<QueryDelta>();
        var fasterQueries = new List<QueryDelta>();

        foreach (var (fingerprint, p2Group) in period2ByFingerprint)
        {
            if (!period1ByFingerprint.TryGetValue(fingerprint, out var p1Group))
                continue; // new query, handled separately

            var p1Avg = p1Group.Average(e => (double)e.DurationUs);
            var p2Avg = p2Group.Average(e => (double)e.DurationUs);

            if (p1Avg <= 0)
                continue;

            var changePercent = ((p2Avg - p1Avg) / p1Avg) * 100.0;

            if (p2Avg > p1Avg)
            {
                slowerQueries.Add(new QueryDelta
                {
                    QueryFingerprint = fingerprint,
                    SampleSql = p2Group[0].SqlText,
                    Period1AvgDurationUs = p1Avg,
                    Period2AvgDurationUs = p2Avg,
                    ChangePercent = changePercent
                });
            }
            else if (p2Avg < p1Avg)
            {
                fasterQueries.Add(new QueryDelta
                {
                    QueryFingerprint = fingerprint,
                    SampleSql = p2Group[0].SqlText,
                    Period1AvgDurationUs = p1Avg,
                    Period2AvgDurationUs = p2Avg,
                    ChangePercent = changePercent
                });
            }
        }

        slowerQueries.Sort((a, b) => b.ChangePercent.CompareTo(a.ChangePercent));
        fasterQueries.Sort((a, b) => a.ChangePercent.CompareTo(b.ChangePercent));

        var newFingerprints = period2ByFingerprint.Keys
            .Except(period1ByFingerprint.Keys)
            .ToList();

        var disappearedFingerprints = period1ByFingerprint.Keys
            .Except(period2ByFingerprint.Keys)
            .ToList();

        return new PeriodComparison
        {
            Period1 = summary1,
            Period2 = summary2,
            SlowerQueries = slowerQueries,
            FasterQueries = fasterQueries,
            NewQueryFingerprints = newFingerprints,
            DisappearedQueryFingerprints = disappearedFingerprints
        };
    }

    /// <inheritdoc/>
    public async Task<List<AnomalyResult>> DetectAnomaliesAsync(
        string serverName, string connectionString, string sessionName,
        double thresholdMultiplier = 2.0,
        CancellationToken ct = default)
    {
        var events = await _profilerService.GetEventsAsync(
            connectionString, sessionName,
            new EventFilters(),
            ct: ct);

        if (events.Count == 0)
            return [];

        var byFingerprint = GroupByFingerprint(events);
        var anomalies = new List<AnomalyResult>();

        foreach (var (fingerprint, group) in byFingerprint)
        {
            var history = await _memoryService.GetQueryHistoryAsync(serverName, fingerprint, ct);

            if (history is null)
                continue;

            // Require sufficient capture history for a reliable baseline
            if (history.CaptureCount < 3)
                continue;

            var currentAvgDuration = group.Average(e => (double)e.DurationUs);
            var historicalAvgDuration = history.AvgDurationUs;

            if (historicalAvgDuration > 0)
            {
                var deviationFactor = currentAvgDuration / historicalAvgDuration;

                if (deviationFactor >= thresholdMultiplier)
                {
                    var severity = deviationFactor >= 5.0 ? "Critical" : "Warning";
                    anomalies.Add(new AnomalyResult
                    {
                        QueryFingerprint = fingerprint,
                        SampleSql = group[0].SqlText,
                        Metric = "Duration",
                        CurrentValue = currentAvgDuration,
                        HistoricalAvg = historicalAvgDuration,
                        DeviationFactor = deviationFactor,
                        Severity = severity
                    });
                }
            }

            // Also check reads anomaly
            if (history.TotalExecutions > 0)
            {
                var historicalAvgReads = (double)history.TotalReads / history.TotalExecutions;
                if (historicalAvgReads > 0)
                {
                    var currentAvgReads = group.Average(e => (double)e.LogicalReads);
                    var readsDeviationFactor = currentAvgReads / historicalAvgReads;

                    if (readsDeviationFactor >= thresholdMultiplier)
                    {
                        var severity = readsDeviationFactor >= 5.0 ? "Critical" : "Warning";
                        anomalies.Add(new AnomalyResult
                        {
                            QueryFingerprint = fingerprint,
                            SampleSql = group[0].SqlText,
                            Metric = "Reads",
                            CurrentValue = currentAvgReads,
                            HistoricalAvg = historicalAvgReads,
                            DeviationFactor = readsDeviationFactor,
                            Severity = severity
                        });
                    }
                }
            }
        }

        anomalies.Sort((a, b) => b.DeviationFactor.CompareTo(a.DeviationFactor));
        return anomalies;
    }

    /// <inheritdoc/>
    public async Task<List<QueryTrendPoint>> GetQueryTrendAsync(
        string serverName, string queryFingerprint,
        int bucketCount = 20,
        CancellationToken ct = default)
    {
        var history = await _memoryService.GetQueryHistoryAsync(serverName, queryFingerprint, ct);

        if (history is null || history.Observations.Count == 0)
            return [];

        var observations = history.Observations
            .OrderBy(o => o.ObservedAt)
            .ToList();

        if (observations.Count > bucketCount)
            observations = observations.Skip(observations.Count - bucketCount).ToList();

        return observations.Select(o => new QueryTrendPoint
        {
            TimeBucket = o.ObservedAt,
            ExecutionCount = o.ExecutionCount,
            AvgDurationUs = o.AvgDurationUs,
            AvgDurationFormatted = ProfilerService.FormatDuration(o.AvgDurationUs),
            AvgReads = o.AvgReads,
            AvgCpuUs = o.AvgCpuUs
        }).ToList();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static Dictionary<string, List<ProfilerEvent>> GroupByFingerprint(List<ProfilerEvent> events)
    {
        var result = new Dictionary<string, List<ProfilerEvent>>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var fp = e.QueryFingerprint;
            if (string.IsNullOrEmpty(fp))
                continue;

            if (!result.TryGetValue(fp, out var list))
            {
                list = [];
                result[fp] = list;
            }
            list.Add(e);
        }
        return result;
    }

    private static PeriodSummary BuildPeriodSummary(
        DateTime start, DateTime end,
        List<ProfilerEvent> events,
        Dictionary<string, List<ProfilerEvent>> byFingerprint)
    {
        var totalDuration = events.Sum(e => e.DurationUs);
        var totalReads = events.Sum(e => e.LogicalReads);
        var avgDuration = events.Count > 0 ? (double)totalDuration / events.Count : 0.0;

        return new PeriodSummary
        {
            Start = start,
            End = end,
            TotalEvents = events.Count,
            UniqueQueries = byFingerprint.Count,
            TotalDurationUs = totalDuration,
            AvgDurationUs = avgDuration,
            TotalReads = totalReads
        };
    }
}
