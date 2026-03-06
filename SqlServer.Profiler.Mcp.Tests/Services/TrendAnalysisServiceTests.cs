using Moq;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Tests.Services;

public class TrendAnalysisServiceTests
{
    private readonly Mock<IProfilerService> _profilerMock = new();
    private readonly Mock<IMemoryService> _memoryMock = new();
    private readonly Mock<IQueryFingerprintService> _fingerprintMock = new();
    private readonly TrendAnalysisService _service;

    private const string ConnStr = "Server=test;Database=db;";
    private const string Session = "my_session";
    private const string Server = "test-server";

    public TrendAnalysisServiceTests()
    {
        _service = new TrendAnalysisService(
            _profilerMock.Object,
            _memoryMock.Object,
            _fingerprintMock.Object);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ProfilerEvent MakeEvent(
        string fingerprint,
        long durationUs,
        long logicalReads = 0,
        string sql = "SELECT 1") =>
        new()
        {
            QueryFingerprint = fingerprint,
            DurationUs = durationUs,
            LogicalReads = logicalReads,
            SqlText = sql
        };

    private void SetupEvents(DateTime start, DateTime end, List<ProfilerEvent> events)
    {
        _profilerMock
            .Setup(p => p.GetEventsAsync(
                ConnStr, Session,
                It.Is<EventFilters>(f => f.StartTime == start && f.EndTime == end),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);
    }

    private void SetupCurrentEvents(List<ProfilerEvent> events)
    {
        _profilerMock
            .Setup(p => p.GetEventsAsync(
                ConnStr, Session,
                It.Is<EventFilters>(f => f.StartTime == null && f.EndTime == null),
                It.IsAny<List<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);
    }

    private static QueryMemory MakeHistory(
        string fingerprint,
        double avgDurationUs,
        long totalReads = 0,
        int totalExecutions = 10,
        int captureCount = 5,
        List<QueryObservation>? observations = null) =>
        new()
        {
            QueryFingerprint = fingerprint,
            ServerName = Server,
            AvgDurationUs = avgDurationUs,
            TotalReads = totalReads,
            TotalExecutions = totalExecutions,
            CaptureCount = captureCount,
            Observations = observations ?? []
        };

    // ── ComparePeriodsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ComparePeriodsAsync_BothPeriodsHaveEvents_ReturnsCorrectSummaries()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = new DateTime(2025, 1, 2, 1, 0, 0, DateTimeKind.Utc);

        SetupEvents(p1Start, p1End, [MakeEvent("fp1", 1000), MakeEvent("fp2", 2000)]);
        SetupEvents(p2Start, p2End, [MakeEvent("fp1", 1500), MakeEvent("fp2", 2500)]);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Equal(2, result.Period1.TotalEvents);
        Assert.Equal(2, result.Period2.TotalEvents);
        Assert.Equal(2, result.Period1.UniqueQueries);
        Assert.Equal(2, result.Period2.UniqueQueries);
        Assert.Equal(3000L, result.Period1.TotalDurationUs);
        Assert.Equal(4000L, result.Period2.TotalDurationUs);
        Assert.Equal(1500.0, result.Period1.AvgDurationUs);
        Assert.Equal(2000.0, result.Period2.AvgDurationUs);
    }

    [Fact]
    public async Task ComparePeriodsAsync_Period1Empty_AllPeriod2QueriesAreNew()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = new DateTime(2025, 1, 2, 1, 0, 0, DateTimeKind.Utc);

        SetupEvents(p1Start, p1End, []);
        SetupEvents(p2Start, p2End, [MakeEvent("fp1", 1000), MakeEvent("fp2", 2000)]);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Empty(result.SlowerQueries);
        Assert.Empty(result.FasterQueries);
        Assert.Empty(result.DisappearedQueryFingerprints);
        Assert.Contains("fp1", result.NewQueryFingerprints);
        Assert.Contains("fp2", result.NewQueryFingerprints);
    }

    [Fact]
    public async Task ComparePeriodsAsync_Period2Empty_AllPeriod1QueriesDisappeared()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = new DateTime(2025, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = new DateTime(2025, 1, 2, 1, 0, 0, DateTimeKind.Utc);

        SetupEvents(p1Start, p1End, [MakeEvent("fp1", 1000), MakeEvent("fp2", 2000)]);
        SetupEvents(p2Start, p2End, []);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Empty(result.SlowerQueries);
        Assert.Empty(result.FasterQueries);
        Assert.Empty(result.NewQueryFingerprints);
        Assert.Contains("fp1", result.DisappearedQueryFingerprints);
        Assert.Contains("fp2", result.DisappearedQueryFingerprints);
    }

    [Fact]
    public async Task ComparePeriodsAsync_QueryGotSlower_AppearsInSlowerQueriesWithCorrectChangePercent()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = p1Start.AddHours(1);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = p2Start.AddHours(1);

        // p1 avg = 1000us, p2 avg = 2000us → +100%
        SetupEvents(p1Start, p1End, [MakeEvent("fp1", 1000)]);
        SetupEvents(p2Start, p2End, [MakeEvent("fp1", 2000)]);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Single(result.SlowerQueries);
        var delta = result.SlowerQueries[0];
        Assert.Equal("fp1", delta.QueryFingerprint);
        Assert.Equal(1000.0, delta.Period1AvgDurationUs);
        Assert.Equal(2000.0, delta.Period2AvgDurationUs);
        Assert.Equal(100.0, delta.ChangePercent, precision: 5);
    }

    [Fact]
    public async Task ComparePeriodsAsync_QueryGotFaster_AppearsInFasterQueries()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = p1Start.AddHours(1);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = p2Start.AddHours(1);

        // p1 avg = 2000us, p2 avg = 1000us → -50%
        SetupEvents(p1Start, p1End, [MakeEvent("fp1", 2000)]);
        SetupEvents(p2Start, p2End, [MakeEvent("fp1", 1000)]);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Single(result.FasterQueries);
        var delta = result.FasterQueries[0];
        Assert.Equal("fp1", delta.QueryFingerprint);
        Assert.Equal(-50.0, delta.ChangePercent, precision: 5);
    }

    [Fact]
    public async Task ComparePeriodsAsync_QueryUnchanged_DoesNotAppearInSlowerOrFaster()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = p1Start.AddHours(1);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = p2Start.AddHours(1);

        SetupEvents(p1Start, p1End, [MakeEvent("fp1", 1000)]);
        SetupEvents(p2Start, p2End, [MakeEvent("fp1", 1000)]);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Empty(result.SlowerQueries);
        Assert.Empty(result.FasterQueries);
    }

    [Fact]
    public async Task ComparePeriodsAsync_NewQuery_AppearsInNewQueryFingerprints()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = p1Start.AddHours(1);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = p2Start.AddHours(1);

        SetupEvents(p1Start, p1End, [MakeEvent("fp1", 1000)]);
        SetupEvents(p2Start, p2End, [MakeEvent("fp1", 1000), MakeEvent("fp_new", 500)]);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Contains("fp_new", result.NewQueryFingerprints);
        Assert.DoesNotContain("fp1", result.NewQueryFingerprints);
    }

    [Fact]
    public async Task ComparePeriodsAsync_DisappearedQuery_AppearsInDisappearedQueryFingerprints()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = p1Start.AddHours(1);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = p2Start.AddHours(1);

        SetupEvents(p1Start, p1End, [MakeEvent("fp1", 1000), MakeEvent("fp_gone", 500)]);
        SetupEvents(p2Start, p2End, [MakeEvent("fp1", 1000)]);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Contains("fp_gone", result.DisappearedQueryFingerprints);
        Assert.DoesNotContain("fp1", result.DisappearedQueryFingerprints);
    }

    [Fact]
    public async Task ComparePeriodsAsync_MultipleQueriesMixed_CorrectlyClassified()
    {
        var p1Start = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var p1End = p1Start.AddHours(1);
        var p2Start = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var p2End = p2Start.AddHours(1);

        SetupEvents(p1Start, p1End,
        [
            MakeEvent("fp_slower", 1000),
            MakeEvent("fp_faster", 2000),
            MakeEvent("fp_gone", 500),
            MakeEvent("fp_same", 800)
        ]);
        SetupEvents(p2Start, p2End,
        [
            MakeEvent("fp_slower", 3000),
            MakeEvent("fp_faster", 500),
            MakeEvent("fp_new", 300),
            MakeEvent("fp_same", 800)
        ]);

        var result = await _service.ComparePeriodsAsync(ConnStr, Session, p1Start, p1End, p2Start, p2End);

        Assert.Single(result.SlowerQueries);
        Assert.Equal("fp_slower", result.SlowerQueries[0].QueryFingerprint);

        Assert.Single(result.FasterQueries);
        Assert.Equal("fp_faster", result.FasterQueries[0].QueryFingerprint);

        Assert.Contains("fp_new", result.NewQueryFingerprints);
        Assert.Contains("fp_gone", result.DisappearedQueryFingerprints);

        Assert.DoesNotContain("fp_same", result.SlowerQueries.Select(q => q.QueryFingerprint));
        Assert.DoesNotContain("fp_same", result.FasterQueries.Select(q => q.QueryFingerprint));
    }

    // ── DetectAnomaliesAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task DetectAnomaliesAsync_NoEvents_ReturnsEmptyList()
    {
        SetupCurrentEvents([]);

        var result = await _service.DetectAnomaliesAsync(Server, ConnStr, Session);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DetectAnomaliesAsync_NoHistoricalData_ReturnsEmptyList()
    {
        SetupCurrentEvents([MakeEvent("fp1", 5000)]);

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryMemory?)null);

        var result = await _service.DetectAnomaliesAsync(Server, ConnStr, Session);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DetectAnomaliesAsync_HistoricalDataFewerThan3Captures_SkipsFingerprint()
    {
        SetupCurrentEvents([MakeEvent("fp1", 5000)]);

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 1000, captureCount: 2));

        var result = await _service.DetectAnomaliesAsync(Server, ConnStr, Session, thresholdMultiplier: 2.0);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DetectAnomaliesAsync_Duration3xHistoricalWithThreshold2_ReturnsWarning()
    {
        SetupCurrentEvents([MakeEvent("fp1", 3000)]);

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 1000, captureCount: 5));

        var result = await _service.DetectAnomaliesAsync(Server, ConnStr, Session, thresholdMultiplier: 2.0);

        var durationAnomaly = result.FirstOrDefault(a => a.Metric == "Duration");
        Assert.NotNull(durationAnomaly);
        Assert.Equal("fp1", durationAnomaly.QueryFingerprint);
        Assert.Equal("Warning", durationAnomaly.Severity);
        Assert.Equal(3.0, durationAnomaly.DeviationFactor, precision: 5);
    }

    [Fact]
    public async Task DetectAnomaliesAsync_Duration6xHistoricalWithThreshold2_ReturnsCritical()
    {
        SetupCurrentEvents([MakeEvent("fp1", 6000)]);

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 1000, captureCount: 5));

        var result = await _service.DetectAnomaliesAsync(Server, ConnStr, Session, thresholdMultiplier: 2.0);

        var durationAnomaly = result.FirstOrDefault(a => a.Metric == "Duration");
        Assert.NotNull(durationAnomaly);
        Assert.Equal("Critical", durationAnomaly.Severity);
        Assert.Equal(6.0, durationAnomaly.DeviationFactor, precision: 5);
    }

    [Fact]
    public async Task DetectAnomaliesAsync_Duration1_5xHistoricalWithThreshold2_NoAnomaly()
    {
        SetupCurrentEvents([MakeEvent("fp1", 1500)]);

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 1000, captureCount: 5));

        var result = await _service.DetectAnomaliesAsync(Server, ConnStr, Session, thresholdMultiplier: 2.0);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DetectAnomaliesAsync_MultipleAnomalies_SortedByDeviationFactorDescending()
    {
        SetupCurrentEvents(
        [
            MakeEvent("fp1", 4000),  // 4x deviation
            MakeEvent("fp2", 10000)  // 10x deviation
        ]);

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 1000, captureCount: 5));

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp2", avgDurationUs: 1000, captureCount: 5));

        var result = await _service.DetectAnomaliesAsync(Server, ConnStr, Session, thresholdMultiplier: 2.0);

        var durationAnomalies = result.Where(a => a.Metric == "Duration").ToList();
        Assert.Equal(2, durationAnomalies.Count);
        Assert.True(durationAnomalies[0].DeviationFactor >= durationAnomalies[1].DeviationFactor,
            "Anomalies should be sorted by deviation factor descending");
        Assert.Equal("fp2", durationAnomalies[0].QueryFingerprint);
    }

    [Fact]
    public async Task DetectAnomaliesAsync_ReadsAnomalyDetected_AlongsideDurationAnomaly()
    {
        // Current event: high duration AND high reads
        SetupCurrentEvents([MakeEvent("fp1", durationUs: 5000, logicalReads: 10000)]);

        // Historical: low duration AND low reads
        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory(
                "fp1",
                avgDurationUs: 1000,
                totalReads: 100,    // historicalAvgReads = 100/10 = 10
                totalExecutions: 10,
                captureCount: 5));

        var result = await _service.DetectAnomaliesAsync(Server, ConnStr, Session, thresholdMultiplier: 2.0);

        var durationAnomaly = result.FirstOrDefault(a => a.Metric == "Duration");
        var readsAnomaly = result.FirstOrDefault(a => a.Metric == "Reads");

        Assert.NotNull(durationAnomaly);
        Assert.NotNull(readsAnomaly);
        Assert.Equal("fp1", readsAnomaly.QueryFingerprint);
        // currentAvgReads = avg of [10000] = 10000; historicalAvgReads = 100/10 = 10; deviation = 1000x
        Assert.Equal(10000.0, readsAnomaly.CurrentValue, precision: 5);
        Assert.Equal(10.0, readsAnomaly.HistoricalAvg, precision: 5);
        Assert.Equal(1000.0, readsAnomaly.DeviationFactor, precision: 5);
        Assert.Equal("Critical", readsAnomaly.Severity);
    }

    // ── GetQueryTrendAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetQueryTrendAsync_NoHistory_ReturnsEmptyList()
    {
        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryMemory?)null);

        var result = await _service.GetQueryTrendAsync(Server, "fp1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetQueryTrendAsync_NoObservations_ReturnsEmptyList()
    {
        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 1000, observations: []));

        var result = await _service.GetQueryTrendAsync(Server, "fp1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetQueryTrendAsync_ObservationsMappedCorrectlyToTrendPoints()
    {
        var obs = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var observations = new List<QueryObservation>
        {
            new()
            {
                CaptureId = "cap1",
                ObservedAt = obs,
                ExecutionCount = 5,
                AvgDurationUs = 2500,
                MaxDurationUs = 5000,
                AvgReads = 100,
                AvgCpuUs = 800
            }
        };

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 2500, observations: observations));

        var result = await _service.GetQueryTrendAsync(Server, "fp1");

        Assert.Single(result);
        var point = result[0];
        Assert.Equal(obs, point.TimeBucket);
        Assert.Equal(5, point.ExecutionCount);
        Assert.Equal(2500L, point.AvgDurationUs);
        Assert.Equal(100L, point.AvgReads);
        Assert.Equal(800L, point.AvgCpuUs);
        Assert.Equal("2.50ms", point.AvgDurationFormatted);
    }

    [Fact]
    public async Task GetQueryTrendAsync_MoreObservationsThanBucketCount_ReturnsOnlyLastN()
    {
        var base_ = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var observations = Enumerable.Range(0, 30)
            .Select(i => new QueryObservation
            {
                CaptureId = $"cap{i}",
                ObservedAt = base_.AddHours(i),
                ExecutionCount = 1,
                AvgDurationUs = 1000L + i,
                MaxDurationUs = 2000,
                AvgReads = 10,
                AvgCpuUs = 500
            })
            .ToList();

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 1000, observations: observations));

        var result = await _service.GetQueryTrendAsync(Server, "fp1", bucketCount: 10);

        Assert.Equal(10, result.Count);
        // Should be the last 10 (indices 20-29), so AvgDurationUs starts at 1020
        Assert.Equal(1020L, result[0].AvgDurationUs);
        Assert.Equal(1029L, result[9].AvgDurationUs);
    }

    [Fact]
    public async Task GetQueryTrendAsync_FewerObservationsThanBucketCount_ReturnsAll()
    {
        var base_ = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var observations = Enumerable.Range(0, 5)
            .Select(i => new QueryObservation
            {
                CaptureId = $"cap{i}",
                ObservedAt = base_.AddHours(i),
                ExecutionCount = 1,
                AvgDurationUs = 1000,
                MaxDurationUs = 2000,
                AvgReads = 10,
                AvgCpuUs = 500
            })
            .ToList();

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 1000, observations: observations));

        var result = await _service.GetQueryTrendAsync(Server, "fp1", bucketCount: 20);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task GetQueryTrendAsync_DurationFormatting_Microseconds()
    {
        var observations = new List<QueryObservation>
        {
            new() { CaptureId = "c1", ObservedAt = DateTime.UtcNow, ExecutionCount = 1,
                    AvgDurationUs = 500, MaxDurationUs = 500, AvgReads = 0, AvgCpuUs = 0 }
        };

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 500, observations: observations));

        var result = await _service.GetQueryTrendAsync(Server, "fp1");

        Assert.Equal("500µs", result[0].AvgDurationFormatted);
    }

    [Fact]
    public async Task GetQueryTrendAsync_DurationFormatting_Milliseconds()
    {
        var observations = new List<QueryObservation>
        {
            new() { CaptureId = "c1", ObservedAt = DateTime.UtcNow, ExecutionCount = 1,
                    AvgDurationUs = 3500, MaxDurationUs = 3500, AvgReads = 0, AvgCpuUs = 0 }
        };

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 3500, observations: observations));

        var result = await _service.GetQueryTrendAsync(Server, "fp1");

        Assert.Equal("3.50ms", result[0].AvgDurationFormatted);
    }

    [Fact]
    public async Task GetQueryTrendAsync_DurationFormatting_Seconds()
    {
        var observations = new List<QueryObservation>
        {
            new() { CaptureId = "c1", ObservedAt = DateTime.UtcNow, ExecutionCount = 1,
                    AvgDurationUs = 2_500_000, MaxDurationUs = 2_500_000, AvgReads = 0, AvgCpuUs = 0 }
        };

        _memoryMock
            .Setup(m => m.GetQueryHistoryAsync(Server, "fp1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeHistory("fp1", avgDurationUs: 2_500_000, observations: observations));

        var result = await _service.GetQueryTrendAsync(Server, "fp1");

        Assert.Equal("2.50s", result[0].AvgDurationFormatted);
    }
}
