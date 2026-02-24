using Microsoft.Extensions.Logging;
using Moq;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Tests.Services;

public class MemoryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ILogger<MemoryService>> _loggerMock = new();
    private readonly string? _originalMemoryPath;
    private readonly string? _originalMemoryEnabled;

    public MemoryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sqlsentinel_test_{Guid.NewGuid():N}");
        _originalMemoryPath = Environment.GetEnvironmentVariable("SQLSENTINEL_MEMORY_PATH");
        _originalMemoryEnabled = Environment.GetEnvironmentVariable("SQLSENTINEL_MEMORY_ENABLED");
        Environment.SetEnvironmentVariable("SQLSENTINEL_MEMORY_PATH", _tempDir);
        Environment.SetEnvironmentVariable("SQLSENTINEL_MEMORY_ENABLED", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQLSENTINEL_MEMORY_PATH", _originalMemoryPath);
        Environment.SetEnvironmentVariable("SQLSENTINEL_MEMORY_ENABLED", _originalMemoryEnabled);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private MemoryService CreateService() => new(_loggerMock.Object);

    // ── Static Helpers ──────────────────────────────

    [Fact]
    public void ExtractServerName_ValidConnectionString()
    {
        var result = MemoryService.ExtractServerName("Server=myserver;Database=mydb;");
        Assert.Equal("myserver", result);
    }

    [Fact]
    public void ExtractServerName_InvalidConnectionString_ReturnsUnknown()
    {
        var result = MemoryService.ExtractServerName("not a connection string");
        Assert.Equal("unknown", result);
    }

    [Fact]
    public void ExtractServerName_EmptyString_ReturnsValue()
    {
        // SqlConnectionStringBuilder accepts empty string; DataSource defaults to ""
        var result = MemoryService.ExtractServerName("");
        Assert.NotNull(result);
    }

    [Fact]
    public void HashServerName_Deterministic()
    {
        var hash1 = MemoryService.HashServerName("myserver");
        var hash2 = MemoryService.HashServerName("myserver");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashServerName_CaseInsensitive()
    {
        var hash1 = MemoryService.HashServerName("MyServer");
        var hash2 = MemoryService.HashServerName("myserver");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashServerName_DifferentServers_DifferentHashes()
    {
        var hash1 = MemoryService.HashServerName("server1");
        var hash2 = MemoryService.HashServerName("server2");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void HashServerName_Returns12CharHex()
    {
        var hash = MemoryService.HashServerName("test");
        Assert.Equal(12, hash.Length);
        Assert.Matches("^[0-9a-f]{12}$", hash);
    }

    [Fact]
    public void SanitizeFilename_ValidName_Unchanged()
    {
        Assert.Equal("test_file", MemoryService.SanitizeFilename("test_file"));
    }

    [Fact]
    public void SanitizeFilename_InvalidChars_Replaced()
    {
        // Get the actual invalid chars for this platform and use one
        var invalidChars = Path.GetInvalidFileNameChars();
        if (invalidChars.Length == 0)
        {
            // No invalid chars on this platform — nothing to test
            return;
        }
        var invalidChar = invalidChars[0];
        var input = $"test{invalidChar}file";
        var result = MemoryService.SanitizeFilename(input);
        // Invalid char should be replaced with underscore
        Assert.Equal("test_file", result);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("short", "short")]
    public void Truncate_ShortOrEmpty(string? input, string expected)
    {
        Assert.Equal(expected, MemoryService.Truncate(input!, 10));
    }

    [Fact]
    public void Truncate_LongString_Truncated()
    {
        var result = MemoryService.Truncate("Hello World!", 5);
        Assert.Equal("Hello", result);
    }

    // ── StartAsync ──────────────────────────────────

    [Fact]
    public async Task StartAsync_CreatesDirectories()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        Assert.True(Directory.Exists(_tempDir));
        Assert.True(Directory.Exists(Path.Combine(_tempDir, "queries")));
    }

    [Fact]
    public async Task StartAsync_Disabled_SkipsDirectoryCreation()
    {
        Environment.SetEnvironmentVariable("SQLSENTINEL_MEMORY_ENABLED", "false");
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        // Directory may or may not exist depending on constructor, but service is disabled
        // Verify disabled behavior via SaveCapture returning disabled marker
        var result = await service.SaveCaptureAsync("srv", "sess", []);
        Assert.Equal("disabled", result.Id);
    }

    // ── SaveCaptureAsync ────────────────────────────

    [Fact]
    public async Task SaveCaptureAsync_WritesFile()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var events = new List<ProfilerEvent>
        {
            new()
            {
                EventName = "sql_batch_completed",
                SqlText = "SELECT 1",
                DurationUs = 1000,
                DatabaseName = "testdb",
                QueryFingerprint = "fp1"
            }
        };

        var result = await service.SaveCaptureAsync("localhost", "test-session", events);

        Assert.NotNull(result);
        Assert.NotEqual("disabled", result.Id);
        Assert.NotEqual("skipped", result.Id);
        Assert.Equal("localhost", result.ServerName);
        Assert.Equal(1, result.TotalEvents);
    }

    [Fact]
    public async Task SaveCaptureAsync_DeduplicatesWithin60Seconds()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var events = new List<ProfilerEvent>
        {
            new() { SqlText = "SELECT 1", QueryFingerprint = "fp1" }
        };

        var first = await service.SaveCaptureAsync("srv", "sess", events);
        Assert.NotEqual("skipped", first.Id);

        var second = await service.SaveCaptureAsync("srv", "sess", events);
        Assert.Equal("skipped", second.Id);
    }

    // ── GetCapturesAsync ────────────────────────────

    [Fact]
    public async Task GetCapturesAsync_ReturnsEmpty_WhenNoCaptures()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var results = await service.GetCapturesAsync();
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetCapturesAsync_ReturnsSavedCaptures()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var events = new List<ProfilerEvent>
        {
            new() { SqlText = "SELECT 1", QueryFingerprint = "fp1" }
        };

        await service.SaveCaptureAsync("srv1", "sess1", events);

        var results = await service.GetCapturesAsync();
        Assert.Single(results);
        Assert.Equal("srv1", results[0].ServerName);
    }

    [Fact]
    public async Task GetCapturesAsync_FiltersReturnsEmpty_WhenDisabled()
    {
        Environment.SetEnvironmentVariable("SQLSENTINEL_MEMORY_ENABLED", "false");
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var results = await service.GetCapturesAsync();
        Assert.Empty(results);
    }

    // ── GetCaptureByIdAsync ─────────────────────────

    [Fact]
    public async Task GetCaptureByIdAsync_ReturnsNull_WhenNotFound()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var result = await service.GetCaptureByIdAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCaptureByIdAsync_ReturnsCapture_WhenFound()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var events = new List<ProfilerEvent>
        {
            new() { SqlText = "SELECT 1", QueryFingerprint = "fp1" }
        };

        var saved = await service.SaveCaptureAsync("srv", "sess", events);
        var result = await service.GetCaptureByIdAsync(saved.Id);

        Assert.NotNull(result);
        Assert.Equal(saved.Id, result.Id);
    }

    // ── PurgeServerMemoryAsync ──────────────────────

    [Fact]
    public async Task PurgeServerMemoryAsync_RemovesServerData()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var events = new List<ProfilerEvent>
        {
            new() { SqlText = "SELECT 1", QueryFingerprint = "fp1" }
        };

        await service.SaveCaptureAsync("srv-to-purge", "sess", events);

        var beforePurge = await service.GetCapturesAsync(serverName: "srv-to-purge");
        Assert.Single(beforePurge);

        await service.PurgeServerMemoryAsync("srv-to-purge");

        var afterPurge = await service.GetCapturesAsync(serverName: "srv-to-purge");
        Assert.Empty(afterPurge);
    }

    // ── GetMemoryStatsAsync ─────────────────────────

    [Fact]
    public async Task GetMemoryStatsAsync_EmptyStorage()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var stats = await service.GetMemoryStatsAsync();
        Assert.Equal(0, stats.TotalCaptures);
    }

    [Fact]
    public async Task GetMemoryStatsAsync_WithCaptures()
    {
        using var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        var events = new List<ProfilerEvent>
        {
            new() { SqlText = "SELECT 1", QueryFingerprint = "fp1" }
        };

        await service.SaveCaptureAsync("srv1", "sess1", events);

        var stats = await service.GetMemoryStatsAsync();
        Assert.Equal(1, stats.TotalCaptures);
        Assert.True(stats.DiskUsageBytes > 0);
    }
}
