using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SqlServer.Profiler.Mcp.Api.Models;
using SqlServer.Profiler.Mcp.Services;
using SqlServer.Profiler.Mcp.Tools;
using SqlServer.Profiler.Mcp.Utilities;

namespace SqlServer.Profiler.Mcp.Api.Controllers;

/// <summary>
/// REST API endpoints that map 1:1 to SQL Sentinel MCP tools for debugging without an AI agent.
/// </summary>
[ApiController]
[Route("api")]
public class ProfilerController : ControllerBase
{
    private readonly SessionManagementTools _sessionTools;
    private readonly EventRetrievalTools _eventTools;
    private readonly PermissionTools _permissionTools;
    private readonly DiagnosticTools _diagnosticTools;
    private readonly MemoryTools _memoryTools;
    private readonly IEventStreamingService _streamingService;

    public ProfilerController(
        SessionManagementTools sessionTools,
        EventRetrievalTools eventTools,
        PermissionTools permissionTools,
        DiagnosticTools diagnosticTools,
        MemoryTools memoryTools,
        IEventStreamingService streamingService)
    {
        _sessionTools = sessionTools;
        _eventTools = eventTools;
        _permissionTools = permissionTools;
        _diagnosticTools = diagnosticTools;
        _memoryTools = memoryTools;
        _streamingService = streamingService;
    }

    private ContentResult JsonContent(string json)
    {
        return Content(json, "application/json");
    }

    // ──────────────────────────────────────────────
    // Session Management
    // ──────────────────────────────────────────────

    /// <summary>
    /// Create a new SQL Server profiling session using Extended Events.
    /// The session is created but not started.
    /// </summary>
    [HttpPost("sessions")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest request)
    {
        var result = await _sessionTools.CreateSession(
            request.SessionName,
            request.Databases,
            request.Applications,
            request.Logins,
            request.Hosts,
            request.MinDurationMs,
            request.ExcludeNoise,
            request.ExcludePatterns,
            request.RingBufferMb,
            request.EventTypes);
        return JsonContent(result);
    }

    /// <summary>
    /// Start capturing events for an existing profiling session.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    [HttpPost("sessions/{sessionName}/start")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> StartSession(
        [FromRoute] string sessionName)
    {
        var result = await _sessionTools.StartSession(sessionName);
        return JsonContent(result);
    }

    /// <summary>
    /// Stop capturing events for a profiling session. Events captured so far are retained.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    [HttpPost("sessions/{sessionName}/stop")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> StopSession(
        [FromRoute] string sessionName)
    {
        var result = await _sessionTools.StopSession(sessionName);
        return JsonContent(result);
    }

    /// <summary>
    /// Drop a profiling session and discard all captured events.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    [HttpDelete("sessions/{sessionName}")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> DropSession(
        [FromRoute] string sessionName)
    {
        var result = await _sessionTools.DropSession(sessionName);
        return JsonContent(result);
    }

    /// <summary>
    /// List all profiling sessions created by this MCP server.
    /// </summary>
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> ListSessions()
    {
        var result = await _sessionTools.ListSessions();
        return JsonContent(result);
    }

    /// <summary>
    /// Create AND start a profiling session in one step for rapid debugging.
    /// </summary>
    [HttpPost("sessions/quick-capture")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> QuickCapture([FromBody] QuickCaptureRequest request)
    {
        var result = await _sessionTools.QuickCapture(
            request.SessionName,
            request.Databases,
            request.Applications,
            request.Logins,
            request.MinDurationMs,
            request.ExcludeNoise,
            request.RingBufferMb,
            request.EventTypes);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Event Retrieval
    // ──────────────────────────────────────────────

    /// <summary>
    /// Retrieve captured events from a profiling session with filtering.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="startTime">Filter events after this time (ISO format: '2024-01-15T10:30:00').</param>
    /// <param name="endTime">Filter events before this time (ISO format).</param>
    /// <param name="database">Filter to specific database.</param>
    /// <param name="application">Filter to specific application.</param>
    /// <param name="login">Filter to specific login.</param>
    /// <param name="textContains">Filter to queries containing this text.</param>
    /// <param name="textNotContains">Exclude queries containing this text.</param>
    /// <param name="minDurationMs">Minimum duration in milliseconds.</param>
    /// <param name="limit">Maximum events to return (1-10000). Default 100.</param>
    /// <param name="offset">Offset for pagination. Default 0.</param>
    /// <param name="sortBy">Sort order: TimestampAsc, TimestampDesc, DurationAsc, DurationDesc, CpuDesc, ReadsDesc.</param>
    /// <param name="deduplicate">Group identical queries and show counts. Default true.</param>
    /// <param name="responseFormat">Output format: Json or Markdown. Default Json.</param>
    [HttpGet("sessions/{sessionName}/events")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetEvents(
        [FromRoute] string sessionName,
        [FromQuery] string? startTime = null,
        [FromQuery] string? endTime = null,
        [FromQuery] string? database = null,
        [FromQuery] string? application = null,
        [FromQuery] string? login = null,
        [FromQuery] string? textContains = null,
        [FromQuery] string? textNotContains = null,
        [FromQuery] int? minDurationMs = null,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string sortBy = "TimestampAsc",
        [FromQuery] bool deduplicate = true,
        [FromQuery] string responseFormat = "Json")
    {
        var result = await _eventTools.GetEvents(
            sessionName, startTime, endTime, database, application,
            login, textContains, textNotContains, minDurationMs, limit, offset,
            sortBy, deduplicate, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Get aggregate statistics from captured events grouped by query fingerprint, database, application, or login.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="startTime">Analysis window start (ISO format).</param>
    /// <param name="endTime">Analysis window end (ISO format).</param>
    /// <param name="groupBy">Group by: QueryFingerprint, Database, Application, Login, or None.</param>
    /// <param name="topN">Return top N results. Default 20.</param>
    /// <param name="responseFormat">Output format: Json or Markdown. Default Markdown.</param>
    [HttpGet("sessions/{sessionName}/stats")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetStats(
        [FromRoute] string sessionName,
        [FromQuery] string? startTime = null,
        [FromQuery] string? endTime = null,
        [FromQuery] string groupBy = "QueryFingerprint",
        [FromQuery] int topN = 20,
        [FromQuery] string responseFormat = "Markdown")
    {
        var result = await _eventTools.GetStats(
            sessionName, startTime, endTime, groupBy, topN, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Analyze the sequence and timing of queries for a specific operation.
    /// Use correlationId to track queries containing a specific identifier or sessionId to follow a specific SPID.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="startTime">Analysis window start (ISO format).</param>
    /// <param name="endTime">Analysis window end (ISO format).</param>
    /// <param name="correlationId">Text to search for in queries (e.g., order ID, request ID).</param>
    /// <param name="sessionId">Specific SPID/session_id to track.</param>
    /// <param name="responseFormat">Output format: Json or Markdown. Default Markdown.</param>
    [HttpGet("sessions/{sessionName}/sequence")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> AnalyzeSequence(
        [FromRoute] string sessionName,
        [FromQuery] string? startTime = null,
        [FromQuery] string? endTime = null,
        [FromQuery] string? correlationId = null,
        [FromQuery] int? sessionId = null,
        [FromQuery] string responseFormat = "Markdown")
    {
        var result = await _eventTools.AnalyzeSequence(
            sessionName, startTime, endTime, correlationId, sessionId, responseFormat);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Connection Info
    // ──────────────────────────────────────────────

    /// <summary>
    /// Get information about databases, applications, logins, or active sessions on the SQL Server.
    /// </summary>
    /// <param name="infoType">What to retrieve: databases, applications, logins, sessions, or all. Default all.</param>
    [HttpGet("connection-info")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetConnectionInfo(
        [FromQuery] string infoType = "all")
    {
        var result = await _eventTools.GetConnectionInfo(infoType);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Permissions
    // ──────────────────────────────────────────────

    /// <summary>
    /// Check if the current SQL Server login has the required permissions for profiling.
    /// </summary>
    [HttpGet("permissions/check")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> CheckPermissions()
    {
        var result = await _permissionTools.CheckPermissions();
        return JsonContent(result);
    }

    /// <summary>
    /// Grant the required profiling permissions to a specified SQL Server login.
    /// The connection must have elevated privileges (sysadmin or CONTROL SERVER).
    /// </summary>
    [HttpPost("permissions/grant")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GrantPermissions([FromBody] GrantPermissionsRequest request)
    {
        var result = await _permissionTools.GrantPermissions(request.TargetLogin);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Diagnostics
    // ──────────────────────────────────────────────

    /// <summary>
    /// Retrieve deadlock events from a profiling session.
    /// </summary>
    [HttpGet("sessions/{sessionName}/deadlocks")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetDeadlocks(
        [FromRoute] string sessionName,
        [FromQuery] string responseFormat = "Markdown")
    {
        var result = await _diagnosticTools.GetDeadlocks(sessionName, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Retrieve blocked process events from a profiling session.
    /// </summary>
    [HttpGet("sessions/{sessionName}/blocking")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetBlocking(
        [FromRoute] string sessionName,
        [FromQuery] string responseFormat = "Markdown")
    {
        var result = await _diagnosticTools.GetBlocking(sessionName, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Query sys.dm_os_wait_stats. Does not require a profiling session.
    /// </summary>
    [HttpGet("wait-stats")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetWaitStats(
        [FromQuery] int topN = 20,
        [FromQuery] string responseFormat = "Markdown")
    {
        var result = await _diagnosticTools.GetWaitStats(topN, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Comprehensive health check: slow queries, deadlocks, blocking, wait stats, and insights.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> HealthCheck(
        [FromQuery] string? sessionName = null,
        [FromQuery] int slowQueryThresholdMs = 1000,
        [FromQuery] string responseFormat = "Markdown")
    {
        var result = await _diagnosticTools.HealthCheck(sessionName ?? "", slowQueryThresholdMs, responseFormat);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Memory
    // ──────────────────────────────────────────────

    /// <summary>
    /// List past profiling captures stored in memory.
    /// Memory is auto-populated when events are retrieved.
    /// </summary>
    [HttpGet("memory/captures")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> ListCaptures(
        [FromQuery] string? serverName = null,
        [FromQuery] string? sessionName = null,
        [FromQuery] string? tag = null,
        [FromQuery] string? since = null,
        [FromQuery] int limit = 20,
        [FromQuery] string responseFormat = "Json")
    {
        var result = await _memoryTools.ListCaptures(serverName, sessionName, tag, since, limit, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Get full details of a specific past capture by its ID.
    /// </summary>
    [HttpGet("memory/captures/{captureId}")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetCapture(
        [FromRoute] string captureId,
        [FromQuery] string responseFormat = "Json")
    {
        var result = await _memoryTools.GetCapture(captureId, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Get the performance history of a specific query fingerprint across all captures.
    /// </summary>
    [HttpGet("memory/queries/{queryFingerprint}/history")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> QueryHistory(
        [FromRoute] string queryFingerprint,
        [FromQuery] string responseFormat = "Json")
    {
        var result = await _memoryTools.QueryHistory(queryFingerprint, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Search through remembered queries by SQL text, database, execution count, or duration.
    /// </summary>
    [HttpGet("memory/queries/search")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> SearchQueries(
        [FromQuery] string? sqlContains = null,
        [FromQuery] string? database = null,
        [FromQuery] int? minExecutions = null,
        [FromQuery] int? minAvgDurationMs = null,
        [FromQuery] int limit = 20,
        [FromQuery] string responseFormat = "Json")
    {
        var result = await _memoryTools.SearchQueries(sqlContains, database, minExecutions, minAvgDurationMs, limit, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Add tags or a note to a past capture for easier retrieval later.
    /// </summary>
    [HttpPost("memory/captures/{captureId}/tag")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> TagCapture(
        [FromRoute] string captureId,
        [FromQuery] string tags,
        [FromQuery] string? note = null)
    {
        var result = await _memoryTools.TagCapture(captureId, tags, note);
        return JsonContent(result);
    }

    /// <summary>
    /// Show memory system statistics: total captures, disk usage, date range.
    /// </summary>
    [HttpGet("memory/stats")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> MemoryStats(
        [FromQuery] string responseFormat = "Json")
    {
        var result = await _memoryTools.GetMemoryStats(responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Purge memory for a specific server or clean up expired entries. Use 'expired' to only clean up old data.
    /// </summary>
    [HttpDelete("memory/{serverName}")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> PurgeMemory(
        [FromRoute] string serverName)
    {
        var result = await _memoryTools.PurgeMemory(serverName);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Real-Time Streaming (SSE)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Stream captured events in real-time using Server-Sent Events (SSE).
    /// Connect with curl -N or EventSource in JavaScript. Events are pushed as they are captured.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session (must be running).</param>
    /// <param name="database">Filter to specific database.</param>
    /// <param name="application">Filter to specific application.</param>
    /// <param name="textContains">Filter to queries containing this text.</param>
    [HttpGet("sessions/{sessionName}/stream")]
    public async Task StreamEvents(
        [FromRoute] string sessionName,
        [FromQuery] string? database = null,
        [FromQuery] string? application = null,
        [FromQuery] string? textContains = null)
    {
        var connStr = ConnectionStringResolver.Resolve();

        var filters = new EventFilters
        {
            Database = database,
            Application = application,
            TextContains = textContains
        };

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        var streamId = _streamingService.StartStreaming(sessionName, connStr, filters);
        var channel = _streamingService.GetEventChannel(streamId);

        if (channel == null)
        {
            Response.StatusCode = 500;
            await Response.WriteAsync("data: {\"error\":\"Failed to start stream\"}\n\n");
            return;
        }

        try
        {
            // Send initial connection event
            await Response.WriteAsync($"data: {{\"type\":\"connected\",\"streamId\":\"{streamId}\",\"sessionName\":\"{sessionName}\"}}\n\n");
            await Response.Body.FlushAsync();

            await foreach (var evt in channel.ReadAllAsync(HttpContext.RequestAborted))
            {
                var json = JsonSerializer.Serialize(new
                {
                    type = "event",
                    evt.EventName,
                    evt.EventTimestamp,
                    evt.DurationUs,
                    evt.DurationFormatted,
                    evt.CpuTimeUs,
                    evt.LogicalReads,
                    evt.Writes,
                    evt.SqlText,
                    evt.DatabaseName,
                    evt.ClientAppName,
                    evt.LoginName,
                    evt.SessionId,
                    evt.QueryFingerprint
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

                await Response.WriteAsync($"data: {json}\n\n");
                await Response.Body.FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal
        }
        finally
        {
            _streamingService.StopStreaming(streamId);
        }
    }
}
