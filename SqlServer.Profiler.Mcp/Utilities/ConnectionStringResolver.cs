namespace SqlServer.Profiler.Mcp.Utilities;

/// <summary>
/// Resolves connection strings from the SQL_SENTINEL_CONNECTION_STRING environment variable.
/// </summary>
public static class ConnectionStringResolver
{
    private const string EnvVar = "SQL_SENTINEL_CONNECTION_STRING";
    private static bool _trustCertWarningLogged;

    /// <summary>
    /// Returns the connection string from the SQL_SENTINEL_CONNECTION_STRING environment variable.
    /// Throws if the environment variable is not set.
    /// </summary>
    public static string Resolve()
    {
        var envValue = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            WarnIfTrustServerCertificate(envValue);
            return envValue;
        }

        throw new ArgumentException(
            $"No connection string configured. Set the {EnvVar} environment variable.");
    }

    private static void WarnIfTrustServerCertificate(string connectionString)
    {
        if (_trustCertWarningLogged)
            return;

        if (connectionString.Contains("TrustServerCertificate=true", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("TrustServerCertificate=True", StringComparison.OrdinalIgnoreCase))
        {
            _trustCertWarningLogged = true;
            Console.Error.WriteLine(
                "[SECURITY WARNING] Connection string contains TrustServerCertificate=true. " +
                "This disables SSL certificate validation and should only be used in development " +
                "with self-signed certificates. For production, use TrustServerCertificate=false with a valid certificate.");
        }
    }
}
