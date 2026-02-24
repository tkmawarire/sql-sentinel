using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;
using Xunit;

namespace SqlServer.Profiler.Mcp.Tests.Services;

public class SessionConfigStoreTests
{
    private readonly SessionConfigStore _store = new();

    private static SessionConfig CreateConfig(string name = "test") => new()
    {
        SessionName = name
    };

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        Assert.Null(_store.Get("nonexistent"));
    }

    [Fact]
    public void Set_ThenGet_ReturnsConfig()
    {
        var config = CreateConfig("session1");
        _store.Set("session1", config);

        var result = _store.Get("session1");
        Assert.NotNull(result);
        Assert.Equal("session1", result.SessionName);
    }

    [Fact]
    public void Set_Overwrite_ReturnsLatestConfig()
    {
        var config1 = CreateConfig("session1");
        var config2 = new SessionConfig { SessionName = "session1", MinDurationMs = 100 };

        _store.Set("session1", config1);
        _store.Set("session1", config2);

        var result = _store.Get("session1");
        Assert.NotNull(result);
        Assert.Equal(100, result.MinDurationMs);
    }

    [Fact]
    public void Remove_ThenGet_ReturnsNull()
    {
        _store.Set("session1", CreateConfig("session1"));
        _store.Remove("session1");

        Assert.Null(_store.Get("session1"));
    }

    [Fact]
    public void Remove_NonExistent_DoesNotThrow()
    {
        _store.Remove("nonexistent"); // Should not throw
    }

    [Fact]
    public void GetSessionNames_EmptyInitially()
    {
        Assert.Empty(_store.GetSessionNames());
    }

    [Fact]
    public void GetSessionNames_ReturnsAllKeys()
    {
        _store.Set("a", CreateConfig("a"));
        _store.Set("b", CreateConfig("b"));
        _store.Set("c", CreateConfig("c"));

        var names = _store.GetSessionNames().OrderBy(n => n).ToList();
        Assert.Equal(["a", "b", "c"], names);
    }

    [Fact]
    public void ConcurrentAccess_DoesNotThrow()
    {
        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            var name = $"session_{i}";
            _store.Set(name, CreateConfig(name));
            _store.Get(name);
            if (i % 3 == 0) _store.Remove(name);
        }));

        Task.WaitAll(tasks.ToArray());
        // Just verify no exceptions were thrown
    }
}
