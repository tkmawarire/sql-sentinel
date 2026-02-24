using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;
using Xunit;

namespace SqlServer.Profiler.Mcp.Tests.Models;

public class ProfilerModelsTests
{
    [Fact]
    public void SessionConfig_RequiredAndDefaults()
    {
        var config = new SessionConfig { SessionName = "test" };

        Assert.Equal("test", config.SessionName);
        Assert.Empty(config.Databases);
        Assert.Empty(config.Applications);
        Assert.Empty(config.Logins);
        Assert.Empty(config.Hosts);
        Assert.Equal(0, config.MinDurationMs);
        Assert.Empty(config.IncludePatterns);
        Assert.Empty(config.ExcludePatterns);
        Assert.Equal(2, config.EventTypes.Count); // SqlBatchCompleted, RpcCompleted
        Assert.Equal(50, config.RingBufferMb);
    }

    [Fact]
    public void SessionConfig_WithAllProperties()
    {
        var config = new SessionConfig
        {
            SessionName = "full",
            Databases = ["db1", "db2"],
            Applications = ["app1"],
            Logins = ["login1"],
            Hosts = ["host1"],
            MinDurationMs = 500,
            IncludePatterns = ["SELECT%"],
            ExcludePatterns = ["sp_reset%"],
            EventTypes = [EventType.Deadlock],
            RingBufferMb = 100
        };

        Assert.Equal("full", config.SessionName);
        Assert.Equal(2, config.Databases.Count);
        Assert.Single(config.Applications);
        Assert.Equal(500, config.MinDurationMs);
        Assert.Equal(100, config.RingBufferMb);
    }

    [Fact]
    public void ProfilerEvent_Defaults()
    {
        var evt = new ProfilerEvent();

        Assert.Equal("", evt.EventName);
        Assert.Null(evt.EventTimestamp);
        Assert.Equal(0, evt.DurationUs);
        Assert.Equal(0, evt.CpuTimeUs);
        Assert.Equal(0, evt.LogicalReads);
        Assert.Equal("", evt.SqlText);
        Assert.Equal("", evt.DatabaseName);
        Assert.Equal(1, evt.ExecutionCount);
    }

    [Fact]
    public void ProfilerEvent_WithValues()
    {
        var now = DateTime.UtcNow;
        var evt = new ProfilerEvent
        {
            EventName = "sql_batch_completed",
            EventTimestamp = now,
            DurationUs = 5000,
            CpuTimeUs = 3000,
            LogicalReads = 100,
            SqlText = "SELECT 1",
            DatabaseName = "testdb"
        };

        Assert.Equal("sql_batch_completed", evt.EventName);
        Assert.Equal(now, evt.EventTimestamp);
        Assert.Equal(5000, evt.DurationUs);
    }

    [Fact]
    public void DeadlockEvent_Construction()
    {
        var evt = new DeadlockEvent
        {
            EventTimestamp = DateTime.UtcNow,
            VictimSpid = "52",
            Processes = [
                new DeadlockProcess { ProcessId = "p1", Spid = 52, IsVictim = true },
                new DeadlockProcess { ProcessId = "p2", Spid = 53, IsVictim = false }
            ],
            RawXml = "<deadlock/>"
        };

        Assert.Equal("52", evt.VictimSpid);
        Assert.Equal(2, evt.Processes.Count);
        Assert.True(evt.Processes[0].IsVictim);
        Assert.False(evt.Processes[1].IsVictim);
    }

    [Fact]
    public void BlockingEvent_Construction()
    {
        var evt = new BlockingEvent
        {
            EventTimestamp = DateTime.UtcNow,
            BlockedProcess = new BlockedProcessInfo { Spid = 52, WaitTimeMs = 5000 },
            BlockingProcess = new BlockingProcessInfo { Spid = 53, Status = "sleeping" },
            RawXml = "<blocked/>"
        };

        Assert.Equal(52, evt.BlockedProcess.Spid);
        Assert.Equal(5000, evt.BlockedProcess.WaitTimeMs);
        Assert.Equal(53, evt.BlockingProcess.Spid);
    }

    [Fact]
    public void DbOperationResult_Success()
    {
        var result = new DbOperationResult(true, rowsAffected: 5);
        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(5, result.RowsAffected);
        Assert.Null(result.Data);
    }

    [Fact]
    public void DbOperationResult_Error()
    {
        var result = new DbOperationResult(false, error: "Something failed");
        Assert.False(result.Success);
        Assert.Equal("Something failed", result.Error);
        Assert.Null(result.RowsAffected);
    }

    [Fact]
    public void DbOperationResult_WithData()
    {
        var data = new List<Dictionary<string, object>> { new() { ["col1"] = "val1" } };
        var result = new DbOperationResult(true, data: data);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public void EventFilters_Defaults()
    {
        var filters = new EventFilters();
        Assert.Null(filters.Database);
        Assert.Null(filters.Application);
        Assert.Null(filters.Login);
        Assert.Null(filters.TextContains);
        Assert.Null(filters.TextNotContains);
        Assert.Null(filters.MinDurationMs);
        Assert.Null(filters.StartTime);
        Assert.Null(filters.EndTime);
    }

    [Fact]
    public void HealthInsight_Construction()
    {
        var insight = new HealthInsight
        {
            Severity = "Warning",
            Category = "Performance",
            Message = "Slow queries detected",
            Detail = "5 queries > 1s"
        };

        Assert.Equal("Warning", insight.Severity);
        Assert.Equal("Performance", insight.Category);
        Assert.Equal("Slow queries detected", insight.Message);
        Assert.Equal("5 queries > 1s", insight.Detail);
    }

    [Fact]
    public void WaitStatEntry_Construction()
    {
        var entry = new WaitStatEntry
        {
            WaitType = "PAGEIOLATCH_SH",
            WaitCategory = "I/O",
            WaitingTasksCount = 100,
            WaitTimeMs = 5000
        };

        Assert.Equal("PAGEIOLATCH_SH", entry.WaitType);
        Assert.Equal("I/O", entry.WaitCategory);
        Assert.Equal(100, entry.WaitingTasksCount);
    }

    [Fact]
    public void EventType_EnumValues()
    {
        Assert.Equal(13, Enum.GetValues<EventType>().Length);
        Assert.Equal(EventType.All, Enum.Parse<EventType>("All"));
    }

    [Fact]
    public void SortOrder_EnumValues()
    {
        Assert.Equal(6, Enum.GetValues<SortOrder>().Length);
    }
}
