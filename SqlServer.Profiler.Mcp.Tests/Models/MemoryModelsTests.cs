using System.Text.Json;
using SqlServer.Profiler.Mcp.Models;
using Xunit;

namespace SqlServer.Profiler.Mcp.Tests.Models;

public class MemoryModelsTests
{
    [Fact]
    public void CaptureMemory_Construction()
    {
        var capture = new CaptureMemory
        {
            Id = "cap-001",
            ServerName = "localhost",
            SessionName = "test-session",
            TotalEvents = 50,
            UniqueQueries = 10,
            TotalDurationUs = 1_000_000,
            DatabasesObserved = ["db1", "db2"],
            Tags = ["performance", "review"]
        };

        Assert.Equal("cap-001", capture.Id);
        Assert.Equal("localhost", capture.ServerName);
        Assert.Equal(50, capture.TotalEvents);
        Assert.Equal(2, capture.DatabasesObserved.Count);
        Assert.Equal(2, capture.Tags.Count);
        Assert.Equal("active", capture.State);
    }

    [Fact]
    public void CaptureMemory_SerializationRoundTrip()
    {
        var capture = new CaptureMemory
        {
            Id = "cap-002",
            ServerName = "server1",
            SessionName = "session1",
            TotalEvents = 100,
            UniqueQueries = 25,
            Tags = ["tag1"]
        };

        var json = JsonSerializer.Serialize(capture);
        var deserialized = JsonSerializer.Deserialize<CaptureMemory>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("cap-002", deserialized.Id);
        Assert.Equal("server1", deserialized.ServerName);
        Assert.Equal(100, deserialized.TotalEvents);
        Assert.Single(deserialized.Tags);
    }

    [Fact]
    public void QuerySummary_Construction()
    {
        var summary = new QuerySummary
        {
            QueryFingerprint = "abc123",
            SqlPreview = "SELECT * FROM Users...",
            ExecutionCount = 10,
            TotalDurationUs = 50000,
            AvgDurationUs = 5000,
            MaxDurationUs = 12000,
            TotalReads = 500
        };

        Assert.Equal("abc123", summary.QueryFingerprint);
        Assert.Equal(10, summary.ExecutionCount);
        Assert.Equal(5000, summary.AvgDurationUs);
    }

    [Fact]
    public void QueryMemory_Construction()
    {
        var memory = new QueryMemory
        {
            QueryFingerprint = "fp123",
            ServerName = "srv1",
            NormalizedSqlPreview = "select * from ?",
            TotalExecutions = 50,
            TotalDurationUs = 250000,
            MaxDurationUs = 10000,
            MinDurationUs = 100,
            DatabasesSeen = ["db1"],
            FirstSeenAt = DateTime.UtcNow.AddDays(-7),
            LastSeenAt = DateTime.UtcNow,
            CaptureCount = 5
        };

        Assert.Equal("fp123", memory.QueryFingerprint);
        Assert.Equal(50, memory.TotalExecutions);
        Assert.Equal(5, memory.CaptureCount);
    }

    [Fact]
    public void MemoryStats_Construction()
    {
        var stats = new MemoryStats
        {
            TotalCaptures = 10,
            TotalQueryFingerprints = 100,
            DiskUsageBytes = 1024 * 1024,
            OldestCapture = DateTime.UtcNow.AddDays(-30),
            NewestCapture = DateTime.UtcNow,
            CapturesPerServer = new Dictionary<string, int> { ["srv1"] = 7, ["srv2"] = 3 }
        };

        Assert.Equal(10, stats.TotalCaptures);
        Assert.Equal(100, stats.TotalQueryFingerprints);
        Assert.Equal(2, stats.CapturesPerServer.Count);
    }

    [Fact]
    public void MemoryConfig_Defaults()
    {
        var config = new MemoryConfig();

        Assert.Equal(30, config.DefaultTtlDays);
        Assert.Equal(90, config.TaggedTtlDays);
        Assert.Equal(100, config.MaxMemoryMb);
        Assert.Equal(50, config.MaxQueryObservations);
        Assert.Equal(10, config.MaxCaptureTopQueries);
    }

    [Fact]
    public void MemoryInsight_Construction()
    {
        var insight = new MemoryInsight
        {
            Severity = "High",
            Category = "Performance",
            Message = "Query regression detected",
            QueryFingerprint = "fp456",
            Detail = "Duration increased 3x"
        };

        Assert.Equal("High", insight.Severity);
        Assert.Equal("fp456", insight.QueryFingerprint);
    }

    [Fact]
    public void QueryObservation_Construction()
    {
        var obs = new QueryObservation
        {
            CaptureId = "cap-001",
            ObservedAt = DateTime.UtcNow,
            ExecutionCount = 5,
            AvgDurationUs = 3000,
            MaxDurationUs = 8000,
            AvgReads = 200,
            AvgCpuUs = 1500
        };

        Assert.Equal("cap-001", obs.CaptureId);
        Assert.Equal(5, obs.ExecutionCount);
    }
}
