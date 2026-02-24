using SqlServer.Profiler.Mcp.Services;
using Xunit;

namespace SqlServer.Profiler.Mcp.Tests.Services;

public class ProfilerServiceTests
{
    // ── FormatDuration ──────────────────────────────

    [Theory]
    [InlineData(0, "0µs")]
    [InlineData(500, "500µs")]
    [InlineData(999, "999µs")]
    public void FormatDuration_Microseconds(long us, string expected)
    {
        Assert.Equal(expected, ProfilerService.FormatDuration(us));
    }

    [Theory]
    [InlineData(1000, "1.00ms")]
    [InlineData(1500, "1.50ms")]
    [InlineData(999999, "1000.00ms")]
    public void FormatDuration_Milliseconds(long us, string expected)
    {
        Assert.Equal(expected, ProfilerService.FormatDuration(us));
    }

    [Theory]
    [InlineData(1_000_000, "1.00s")]
    [InlineData(2_500_000, "2.50s")]
    [InlineData(60_000_000, "60.00s")]
    public void FormatDuration_Seconds(long us, string expected)
    {
        Assert.Equal(expected, ProfilerService.FormatDuration(us));
    }

    // ── FormatBytes ─────────────────────────────────

    [Theory]
    [InlineData(0, "0B")]
    [InlineData(500, "500B")]
    [InlineData(1023, "1023B")]
    public void FormatBytes_Bytes(long bytes, string expected)
    {
        Assert.Equal(expected, ProfilerService.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(1024, "1.0KB")]
    [InlineData(1536, "1.5KB")]
    public void FormatBytes_Kilobytes(long bytes, string expected)
    {
        Assert.Equal(expected, ProfilerService.FormatBytes(bytes));
    }

    [Fact]
    public void FormatBytes_Megabytes()
    {
        var result = ProfilerService.FormatBytes(1024 * 1024);
        Assert.Equal("1.0MB", result);
    }

    [Fact]
    public void FormatBytes_Gigabytes()
    {
        var result = ProfilerService.FormatBytes(1024L * 1024 * 1024);
        Assert.Equal("1.00GB", result);
    }

    // ── FormatMilliseconds ──────────────────────────

    [Theory]
    [InlineData(0, "0ms")]
    [InlineData(500, "500ms")]
    [InlineData(999, "999ms")]
    public void FormatMilliseconds_Ms(long ms, string expected)
    {
        Assert.Equal(expected, ProfilerService.FormatMilliseconds(ms));
    }

    [Theory]
    [InlineData(1000, "1.00s")]
    [InlineData(1500, "1.50s")]
    [InlineData(59999, "60.00s")]
    public void FormatMilliseconds_Seconds(long ms, string expected)
    {
        Assert.Equal(expected, ProfilerService.FormatMilliseconds(ms));
    }

    [Fact]
    public void FormatMilliseconds_Minutes()
    {
        Assert.Equal("1.0min", ProfilerService.FormatMilliseconds(60_000));
    }

    [Fact]
    public void FormatMilliseconds_Hours()
    {
        Assert.Equal("1.0hr", ProfilerService.FormatMilliseconds(3_600_000));
    }

    // ── ParseDeadlockXml ────────────────────────────

    [Fact]
    public void ParseDeadlockXml_ValidXml_ParsesProcesses()
    {
        var xml = """
            <deadlock-list>
                <deadlock>
                    <victim-list>
                        <victimProcess id="process1"/>
                    </victim-list>
                    <process-list>
                        <process id="process1" spid="52" loginname="user1" hostname="host1"
                                 clientapp="app1" currentdb="testdb" waitresource="KEY: 5:72057594038845440"
                                 lockMode="X" waittime="5000">
                            <inputbuf>SELECT * FROM Users WHERE Id = 1</inputbuf>
                        </process>
                        <process id="process2" spid="53" loginname="user2" hostname="host2"
                                 clientapp="app2" currentdb="testdb" waitresource="KEY: 5:72057594038845441"
                                 lockMode="S" waittime="3000">
                            <inputbuf>UPDATE Users SET Name = 'test'</inputbuf>
                        </process>
                    </process-list>
                    <resource-list/>
                </deadlock>
            </deadlock-list>
            """;

        var timestamp = new DateTime(2024, 1, 15, 10, 30, 0);
        var result = ProfilerService.ParseDeadlockXml(xml, timestamp);

        Assert.Equal(timestamp, result.EventTimestamp);
        Assert.Equal(2, result.Processes.Count);
        Assert.Equal("52", result.VictimSpid);

        var victim = result.Processes.First(p => p.IsVictim);
        Assert.Equal(52, victim.Spid);
        Assert.Equal("user1", victim.LoginName);
        Assert.Equal("host1", victim.HostName);
        Assert.Contains("SELECT", victim.SqlText);
    }

    [Fact]
    public void ParseDeadlockXml_MinimalXml_HandlesGracefully()
    {
        var xml = "<deadlock><process-list></process-list></deadlock>";
        var result = ProfilerService.ParseDeadlockXml(xml, null);

        Assert.Null(result.EventTimestamp);
        Assert.Empty(result.Processes);
    }

    // ── ParseBlockingXml ────────────────────────────

    [Fact]
    public void ParseBlockingXml_ValidXml_ParsesProcesses()
    {
        var xml = """
            <blocked-process-report>
                <blocked-process>
                    <process spid="52" waittime="5000" loginname="user1" hostname="host1"
                             currentdb="testdb" lockMode="X" waitresource="KEY: 5:123">
                        <inputbuf>SELECT * FROM Orders</inputbuf>
                    </process>
                </blocked-process>
                <blocking-process>
                    <process spid="53" loginname="user2" hostname="host2"
                             currentdb="testdb" status="sleeping">
                        <inputbuf>UPDATE Orders SET Status = 1</inputbuf>
                    </process>
                </blocking-process>
            </blocked-process-report>
            """;

        var timestamp = new DateTime(2024, 1, 15, 10, 30, 0);
        var result = ProfilerService.ParseBlockingXml(xml, timestamp);

        Assert.Equal(timestamp, result.EventTimestamp);
        Assert.Equal(52, result.BlockedProcess.Spid);
        Assert.Equal(5000, result.BlockedProcess.WaitTimeMs);
        Assert.Equal("user1", result.BlockedProcess.LoginName);
        Assert.Equal(53, result.BlockingProcess.Spid);
        Assert.Equal("sleeping", result.BlockingProcess.Status);
    }

    [Fact]
    public void ParseBlockingXml_MissingNodes_DefaultValues()
    {
        var xml = "<blocked-process-report></blocked-process-report>";
        var result = ProfilerService.ParseBlockingXml(xml, null);

        Assert.Equal(0, result.BlockedProcess.Spid);
        Assert.Equal(0, result.BlockingProcess.Spid);
        Assert.Null(result.EventTimestamp);
    }
}
