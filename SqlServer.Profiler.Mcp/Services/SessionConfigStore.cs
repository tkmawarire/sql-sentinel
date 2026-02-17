using System.Collections.Concurrent;
using SqlServer.Profiler.Mcp.Models;

namespace SqlServer.Profiler.Mcp.Services;

/// <summary>
/// In-memory store for session configurations.
/// Allows tools to retrieve exclude patterns and other config for a session.
/// </summary>
public class SessionConfigStore
{
    private readonly ConcurrentDictionary<string, SessionConfig> _configs = new();

    public void Set(string sessionName, SessionConfig config)
    {
        _configs[sessionName] = config;
    }

    public SessionConfig? Get(string sessionName)
    {
        _configs.TryGetValue(sessionName, out var config);
        return config;
    }

    public void Remove(string sessionName)
    {
        _configs.TryRemove(sessionName, out _);
    }

    public IEnumerable<string> GetSessionNames() => _configs.Keys;
}
