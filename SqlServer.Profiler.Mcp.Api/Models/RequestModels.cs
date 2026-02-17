namespace SqlServer.Profiler.Mcp.Api.Models;

/// <summary>
/// Request body for creating a new profiling session.
/// </summary>
public class CreateSessionRequest
{
    /// <summary>
    /// Unique name for this session (alphanumeric + underscore, e.g. 'debug_checkout').
    /// </summary>
    public required string SessionName { get; set; }

    /// <summary>
    /// SQL Server connection string. If not provided, falls back to configured default.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Filter to specific database names (comma-separated). Empty = all databases.
    /// </summary>
    public string? Databases { get; set; }

    /// <summary>
    /// Filter to specific application names from connection string (comma-separated). Empty = all apps.
    /// </summary>
    public string? Applications { get; set; }

    /// <summary>
    /// Filter to specific SQL logins (comma-separated). Empty = all users.
    /// </summary>
    public string? Logins { get; set; }

    /// <summary>
    /// Filter to specific client hostnames (comma-separated). Empty = all hosts.
    /// </summary>
    public string? Hosts { get; set; }

    /// <summary>
    /// Minimum query duration in milliseconds. Use to filter out fast queries. Default 0 = all.
    /// </summary>
    public int MinDurationMs { get; set; } = 0;

    /// <summary>
    /// Auto-exclude common noise (sp_reset_connection, SET statements, etc.). Default true.
    /// </summary>
    public bool ExcludeNoise { get; set; } = true;

    /// <summary>
    /// Additional regex patterns to exclude (comma-separated).
    /// </summary>
    public string? ExcludePatterns { get; set; }

    /// <summary>
    /// Ring buffer size in MB. Larger = more events but more memory. Default 50.
    /// </summary>
    public int RingBufferMb { get; set; } = 50;
}

/// <summary>
/// Request body for quick capture (create + start in one step).
/// </summary>
public class QuickCaptureRequest
{
    /// <summary>
    /// Unique name for this session.
    /// </summary>
    public required string SessionName { get; set; }

    /// <summary>
    /// SQL Server connection string. If not provided, falls back to configured default.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Filter to specific database names (comma-separated).
    /// </summary>
    public string? Databases { get; set; }

    /// <summary>
    /// Filter to specific application names (comma-separated).
    /// </summary>
    public string? Applications { get; set; }

    /// <summary>
    /// Filter to specific SQL logins (comma-separated).
    /// </summary>
    public string? Logins { get; set; }

    /// <summary>
    /// Minimum query duration in milliseconds.
    /// </summary>
    public int MinDurationMs { get; set; } = 0;

    /// <summary>
    /// Auto-exclude common noise. Default true.
    /// </summary>
    public bool ExcludeNoise { get; set; } = true;

    /// <summary>
    /// Ring buffer size in MB. Default 50.
    /// </summary>
    public int RingBufferMb { get; set; } = 50;
}

/// <summary>
/// Request body for granting permissions to a SQL Server login.
/// </summary>
public class GrantPermissionsRequest
{
    /// <summary>
    /// SQL Server connection string (must use a login with sysadmin or CONTROL SERVER). If not provided, falls back to configured default.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The SQL Server login to grant permissions to (e.g., 'app_user' or 'DOMAIN\Username').
    /// </summary>
    public required string TargetLogin { get; set; }
}
