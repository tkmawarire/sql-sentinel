using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Tools;

/// <summary>
/// Default noise patterns to filter out common SQL Server chatter.
/// </summary>
public static class NoisePatterns
{
    public static readonly List<string> Default =
    [
        "sp_reset_connection",
        "sp_executesql.*sys.databases",
        "SELECT.*@@SPID",
        "SET TRANSACTION ISOLATION LEVEL",
        "SET NOCOUNT",
        "SET ANSI_NULLS",
        "SET ANSI_PADDING",
        "SET ANSI_WARNINGS",
        "SET ARITHABORT",
        "SET CONCAT_NULL_YIELDS_NULL",
        "SET QUOTED_IDENTIFIER",
        "SET NUMERIC_ROUNDABORT",
        "sp_trace_",
        "fn_trace_"
    ];
}

/// <summary>
/// MCP Tools for SQL Server profiling session management.
/// </summary>
[McpServerToolType]
public class SessionManagementTools
{
    private readonly IProfilerService _profilerService;
    private readonly SessionConfigStore _configStore;

    public SessionManagementTools(IProfilerService profilerService, SessionConfigStore configStore)
    {
        _profilerService = profilerService;
        _configStore = configStore;
    }

    /// <summary>
    /// Create a new SQL Server profiling session using Extended Events.
    /// The session is created but not started. Use sqlsentinel_start_session to begin capture.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_create_session")]
    [Description("""
        Create a new SQL Server profiling session using Extended Events.
        
        This creates a server-side event session that captures SQL queries, stored procedure calls, 
        and other database events with minimal overhead (~1-2%).
        
        The session is created but NOT started. Use sqlsentinel_start_session to begin capture.
        
        Connection string format: "Server=localhost;Database=master;User Id=sa;Password=xxx;TrustServerCertificate=true"
        Or for Windows auth: "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true"
        """)]
    public async Task<string> CreateSession(
        [Description("Unique name for this session (alphanumeric + underscore, e.g. 'debug_checkout')")] string sessionName,
        [Description("SQL Server connection string")] string connectionString,
        [Description("Filter to specific database names (comma-separated). Empty = all databases.")] string? databases = null,
        [Description("Filter to specific application names from connection string (comma-separated). Empty = all apps.")] string? applications = null,
        [Description("Filter to specific SQL logins (comma-separated). Empty = all users.")] string? logins = null,
        [Description("Filter to specific client hostnames (comma-separated). Empty = all hosts.")] string? hosts = null,
        [Description("Minimum query duration in milliseconds. Use to filter out fast queries. Default 0 = all.")] int minDurationMs = 0,
        [Description("Auto-exclude common noise (sp_reset_connection, SET statements, etc.). Default true.")] bool excludeNoise = true,
        [Description("Additional regex patterns to exclude (comma-separated).")] string? excludePatterns = null,
        [Description("Ring buffer size in MB. Larger = more events but more memory. Default 50.")] int ringBufferMb = 50,
        [Description("Event types to capture (comma-separated). Options: SqlBatchCompleted, RpcCompleted, SqlStatementCompleted, SpStatementCompleted, Attention, ErrorReported, Deadlock, BlockedProcess, LoginEvent, SchemaChange, Recompile, AutoStats, All. Default: SqlBatchCompleted,RpcCompleted")] string? eventTypes = null)
    {
        try
        {
            var allExcludePatterns = excludeNoise ? new List<string>(NoisePatterns.Default) : [];
            if (!string.IsNullOrWhiteSpace(excludePatterns))
            {
                allExcludePatterns.AddRange(excludePatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            var parsedEventTypes = ParseEventTypes(eventTypes);

            var config = new SessionConfig
            {
                SessionName = sessionName,
                Databases = ParseList(databases),
                Applications = ParseList(applications),
                Logins = ParseList(logins),
                Hosts = ParseList(hosts),
                MinDurationMs = minDurationMs,
                ExcludePatterns = allExcludePatterns,
                EventTypes = parsedEventTypes,
                RingBufferMb = ringBufferMb
            };

            var result = await _profilerService.CreateSessionAsync(connectionString, config);
            _configStore.Set(sessionName, config);

            result["config"] = new
            {
                databases = config.Databases,
                applications = config.Applications,
                logins = config.Logins,
                minDurationMs = config.MinDurationMs,
                eventTypes = config.EventTypes.Select(e => e.ToString()),
                excludeNoise,
                ringBufferMb = config.RingBufferMb
            };

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                suggestion = "Check connection string, ensure you have ALTER ANY EVENT SESSION permission, and verify session name doesn't already exist."
            }, JsonOptions.Default);
        }
    }

    /// <summary>
    /// Start capturing events for an existing profiling session.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_start_session")]
    [Description("Start capturing events for an existing profiling session. The session must have been created first with sqlsentinel_create_session.")]
    public async Task<string> StartSession(
        [Description("Name of the profiling session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString)
    {
        try
        {
            var result = await _profilerService.StartSessionAsync(connectionString, sessionName);
            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                suggestion = "Ensure session exists (use sqlsentinel_list_sessions) and you have ALTER ANY EVENT SESSION permission."
            }, JsonOptions.Default);
        }
    }

    /// <summary>
    /// Stop capturing events for a profiling session.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_stop_session")]
    [Description("Stop capturing events for a profiling session. Events captured so far are retained. Use sqlsentinel_drop_session to completely remove the session.")]
    public async Task<string> StopSession(
        [Description("Name of the profiling session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString)
    {
        try
        {
            var result = await _profilerService.StopSessionAsync(connectionString, sessionName);
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

    /// <summary>
    /// Drop a profiling session and discard all captured events.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_drop_session")]
    [Description("Drop a profiling session and discard all captured events. WARNING: This permanently deletes all captured events. Retrieve any needed data with sqlsentinel_get_events before dropping.")]
    public async Task<string> DropSession(
        [Description("Name of the profiling session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString)
    {
        try
        {
            var result = await _profilerService.DropSessionAsync(connectionString, sessionName);
            _configStore.Remove(sessionName);
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

    /// <summary>
    /// List all profiling sessions created by this MCP server.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_list_sessions")]
    [Description("List all profiling sessions created by this MCP server. Shows session name, state (RUNNING/STOPPED), and buffer usage.")]
    public async Task<string> ListSessions(
        [Description("SQL Server connection string")] string connectionString)
    {
        try
        {
            var sessions = await _profilerService.ListSessionsAsync(connectionString);
            return JsonSerializer.Serialize(new
            {
                sessions,
                count = sessions.Count,
                message = $"Found {sessions.Count} SQL Sentinel session(s)"
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

    /// <summary>
    /// Create AND start a profiling session in one step.
    /// </summary>
    [McpServerTool(Name = "sqlsentinel_quick_capture")]
    [Description("Create AND start a profiling session in one step. Convenience tool for rapid debugging. Creates a session with specified filters and immediately begins capture.")]
    public async Task<string> QuickCapture(
        [Description("Unique name for this session")] string sessionName,
        [Description("SQL Server connection string")] string connectionString,
        [Description("Filter to specific database names (comma-separated)")] string? databases = null,
        [Description("Filter to specific application names (comma-separated)")] string? applications = null,
        [Description("Filter to specific SQL logins (comma-separated)")] string? logins = null,
        [Description("Minimum query duration in milliseconds")] int minDurationMs = 0,
        [Description("Auto-exclude common noise")] bool excludeNoise = true,
        [Description("Ring buffer size in MB")] int ringBufferMb = 50,
        [Description("Event types to capture (comma-separated). Default: SqlBatchCompleted,RpcCompleted")] string? eventTypes = null)
    {
        try
        {
            var allExcludePatterns = excludeNoise ? new List<string>(NoisePatterns.Default) : [];
            var parsedEventTypes = ParseEventTypes(eventTypes);

            var config = new SessionConfig
            {
                SessionName = sessionName,
                Databases = ParseList(databases),
                Applications = ParseList(applications),
                Logins = ParseList(logins),
                MinDurationMs = minDurationMs,
                ExcludePatterns = allExcludePatterns,
                EventTypes = parsedEventTypes,
                RingBufferMb = ringBufferMb
            };

            await _profilerService.CreateSessionAsync(connectionString, config);
            var startResult = await _profilerService.StartSessionAsync(connectionString, sessionName);
            _configStore.Set(sessionName, config);

            return JsonSerializer.Serialize(new
            {
                success = true,
                sessionName,
                status = "RUNNING",
                startedAt = startResult["startedAt"],
                message = $"Session '{sessionName}' created and capturing. Use sqlsentinel_get_events to retrieve queries.",
                config = new
                {
                    databases = config.Databases,
                    applications = config.Applications,
                    minDurationMs = config.MinDurationMs,
                    excludeNoise
                }
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

    private static List<string> ParseList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        return input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static List<EventType> ParseEventTypes(string? eventTypes)
    {
        if (string.IsNullOrWhiteSpace(eventTypes))
            return [EventType.SqlBatchCompleted, EventType.RpcCompleted];

        var parsed = new List<EventType>();
        foreach (var et in eventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<EventType>(et, ignoreCase: true, out var eventType))
                parsed.Add(eventType);
        }

        return parsed.Count > 0 ? parsed : [EventType.SqlBatchCompleted, EventType.RpcCompleted];
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
