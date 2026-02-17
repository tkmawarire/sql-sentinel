using Microsoft.AspNetCore.Mvc;
using SqlServer.Profiler.Mcp.Api.Models;
using SqlServer.Profiler.Mcp.Tools;

namespace SqlServer.Profiler.Mcp.Api.Controllers;

/// <summary>
/// REST API endpoints that map 1:1 to SQL Profiler MCP tools for debugging without an AI agent.
/// </summary>
[ApiController]
[Route("api")]
public class ProfilerController : ControllerBase
{
    private readonly SessionManagementTools _sessionTools;
    private readonly EventRetrievalTools _eventTools;
    private readonly PermissionTools _permissionTools;
    private readonly IConfiguration _configuration;

    public ProfilerController(
        SessionManagementTools sessionTools,
        EventRetrievalTools eventTools,
        PermissionTools permissionTools,
        IConfiguration configuration)
    {
        _sessionTools = sessionTools;
        _eventTools = eventTools;
        _permissionTools = permissionTools;
        _configuration = configuration;
    }

    private string ResolveConnectionString(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;
        var configured = _configuration["SqlProfiler:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var envVar = Environment.GetEnvironmentVariable("SQL_PROFILER_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envVar))
            return envVar;
        throw new InvalidOperationException("No connection string provided. Pass connectionString parameter, set SqlProfiler:ConnectionString in config, or set SQL_PROFILER_CONNECTION_STRING environment variable.");
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
        var connStr = ResolveConnectionString(request.ConnectionString);
        var result = await _sessionTools.CreateSession(
            request.SessionName,
            connStr,
            request.Databases,
            request.Applications,
            request.Logins,
            request.Hosts,
            request.MinDurationMs,
            request.ExcludeNoise,
            request.ExcludePatterns,
            request.RingBufferMb);
        return JsonContent(result);
    }

    /// <summary>
    /// Start capturing events for an existing profiling session.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
    [HttpPost("sessions/{sessionName}/start")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> StartSession(
        [FromRoute] string sessionName,
        [FromQuery] string? connectionString = null)
    {
        var connStr = ResolveConnectionString(connectionString);
        var result = await _sessionTools.StartSession(sessionName, connStr);
        return JsonContent(result);
    }

    /// <summary>
    /// Stop capturing events for a profiling session. Events captured so far are retained.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
    [HttpPost("sessions/{sessionName}/stop")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> StopSession(
        [FromRoute] string sessionName,
        [FromQuery] string? connectionString = null)
    {
        var connStr = ResolveConnectionString(connectionString);
        var result = await _sessionTools.StopSession(sessionName, connStr);
        return JsonContent(result);
    }

    /// <summary>
    /// Drop a profiling session and discard all captured events.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
    [HttpDelete("sessions/{sessionName}")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> DropSession(
        [FromRoute] string sessionName,
        [FromQuery] string? connectionString = null)
    {
        var connStr = ResolveConnectionString(connectionString);
        var result = await _sessionTools.DropSession(sessionName, connStr);
        return JsonContent(result);
    }

    /// <summary>
    /// List all profiling sessions created by this MCP server.
    /// </summary>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
    [HttpGet("sessions")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> ListSessions(
        [FromQuery] string? connectionString = null)
    {
        var connStr = ResolveConnectionString(connectionString);
        var result = await _sessionTools.ListSessions(connStr);
        return JsonContent(result);
    }

    /// <summary>
    /// Create AND start a profiling session in one step for rapid debugging.
    /// </summary>
    [HttpPost("sessions/quick-capture")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> QuickCapture([FromBody] QuickCaptureRequest request)
    {
        var connStr = ResolveConnectionString(request.ConnectionString);
        var result = await _sessionTools.QuickCapture(
            request.SessionName,
            connStr,
            request.Databases,
            request.Applications,
            request.Logins,
            request.MinDurationMs,
            request.ExcludeNoise,
            request.RingBufferMb);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Event Retrieval
    // ──────────────────────────────────────────────

    /// <summary>
    /// Retrieve captured events from a profiling session with filtering.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
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
        [FromQuery] string? connectionString = null,
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
        var connStr = ResolveConnectionString(connectionString);
        var result = await _eventTools.GetEvents(
            sessionName, connStr, startTime, endTime, database, application,
            login, textContains, textNotContains, minDurationMs, limit, offset,
            sortBy, deduplicate, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Get aggregate statistics from captured events grouped by query fingerprint, database, application, or login.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
    /// <param name="startTime">Analysis window start (ISO format).</param>
    /// <param name="endTime">Analysis window end (ISO format).</param>
    /// <param name="groupBy">Group by: QueryFingerprint, Database, Application, Login, or None.</param>
    /// <param name="topN">Return top N results. Default 20.</param>
    /// <param name="responseFormat">Output format: Json or Markdown. Default Markdown.</param>
    [HttpGet("sessions/{sessionName}/stats")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetStats(
        [FromRoute] string sessionName,
        [FromQuery] string? connectionString = null,
        [FromQuery] string? startTime = null,
        [FromQuery] string? endTime = null,
        [FromQuery] string groupBy = "QueryFingerprint",
        [FromQuery] int topN = 20,
        [FromQuery] string responseFormat = "Markdown")
    {
        var connStr = ResolveConnectionString(connectionString);
        var result = await _eventTools.GetStats(
            sessionName, connStr, startTime, endTime, groupBy, topN, responseFormat);
        return JsonContent(result);
    }

    /// <summary>
    /// Analyze the sequence and timing of queries for a specific operation.
    /// Use correlationId to track queries containing a specific identifier or sessionId to follow a specific SPID.
    /// </summary>
    /// <param name="sessionName">Name of the profiling session.</param>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
    /// <param name="startTime">Analysis window start (ISO format).</param>
    /// <param name="endTime">Analysis window end (ISO format).</param>
    /// <param name="correlationId">Text to search for in queries (e.g., order ID, request ID).</param>
    /// <param name="sessionId">Specific SPID/session_id to track.</param>
    /// <param name="responseFormat">Output format: Json or Markdown. Default Markdown.</param>
    [HttpGet("sessions/{sessionName}/sequence")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> AnalyzeSequence(
        [FromRoute] string sessionName,
        [FromQuery] string? connectionString = null,
        [FromQuery] string? startTime = null,
        [FromQuery] string? endTime = null,
        [FromQuery] string? correlationId = null,
        [FromQuery] int? sessionId = null,
        [FromQuery] string responseFormat = "Markdown")
    {
        var connStr = ResolveConnectionString(connectionString);
        var result = await _eventTools.AnalyzeSequence(
            sessionName, connStr, startTime, endTime, correlationId, sessionId, responseFormat);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Connection Info
    // ──────────────────────────────────────────────

    /// <summary>
    /// Get information about databases, applications, logins, or active sessions on the SQL Server.
    /// </summary>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
    /// <param name="infoType">What to retrieve: databases, applications, logins, sessions, or all. Default all.</param>
    [HttpGet("connection-info")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> GetConnectionInfo(
        [FromQuery] string? connectionString = null,
        [FromQuery] string infoType = "all")
    {
        var connStr = ResolveConnectionString(connectionString);
        var result = await _eventTools.GetConnectionInfo(connStr, infoType);
        return JsonContent(result);
    }

    // ──────────────────────────────────────────────
    // Permissions
    // ──────────────────────────────────────────────

    /// <summary>
    /// Check if the current SQL Server login has the required permissions for profiling.
    /// </summary>
    /// <param name="connectionString">Optional SQL Server connection string override.</param>
    [HttpGet("permissions/check")]
    [ProducesResponseType(typeof(string), 200)]
    public async Task<IActionResult> CheckPermissions(
        [FromQuery] string? connectionString = null)
    {
        var connStr = ResolveConnectionString(connectionString);
        var result = await _permissionTools.CheckPermissions(connStr);
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
        var connStr = ResolveConnectionString(request.ConnectionString);
        var result = await _permissionTools.GrantPermissions(connStr, request.TargetLogin);
        return JsonContent(result);
    }
}
