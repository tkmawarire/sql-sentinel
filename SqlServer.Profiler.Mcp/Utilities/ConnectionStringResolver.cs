namespace SqlServer.Profiler.Mcp.Utilities;

/// <summary>
/// Resolves connection strings from explicit parameter or environment variable fallback.
/// </summary>
public static class ConnectionStringResolver
{
    private const string EnvVar = "SQL_SENTINEL_CONNECTION_STRING";

    /// <summary>
    /// Returns the connection string from the explicit parameter, or falls back to
    /// the SQL_SENTINEL_CONNECTION_STRING environment variable.
    /// Throws if neither is available.
    /// </summary>
    public static string Resolve(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var envValue = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue;

        throw new ArgumentException(
            $"No connection string provided. Either pass the connectionString parameter or set the {EnvVar} environment variable.");
    }
}
