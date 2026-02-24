using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SqlServer.Profiler.Mcp.Utilities;

/// <summary>
/// Sanitizes exception messages for external consumption.
/// Maps SQL Server error numbers and .NET exception types to safe, generic messages.
/// Full exception details are logged to stderr via ILogger for diagnostics.
/// </summary>
public static class ErrorSanitizer
{
    /// <summary>
    /// Returns a sanitized error message safe for returning to MCP clients.
    /// Logs the full exception details to the provided logger (if any).
    /// </summary>
    public static string Sanitize(Exception ex, ILogger? logger = null, string? context = null)
    {
        logger?.LogError(ex, "Error in {Context}", context ?? "unknown");

        return ex switch
        {
            ArgumentException => ex.Message, // Our own validation messages — safe to pass through
            SqlException sqlEx => SanitizeSqlException(sqlEx),
            InvalidOperationException => "Operation failed. Check session state and connection.",
            TimeoutException => "Operation timed out.",
            OperationCanceledException => "Operation was cancelled.",
            _ => "An unexpected error occurred. Check server logs for details."
        };
    }

    private static string SanitizeSqlException(SqlException ex)
    {
        return ex.Number switch
        {
            -2 => "SQL Server operation timed out.",
            2 or 53 => "Cannot connect to SQL Server. Verify the server address and network connectivity.",
            4060 => "Cannot access the specified database. Verify the database name and permissions.",
            18456 => "Login failed. Verify credentials.",
            229 or 230 => "Insufficient permissions for this operation.",
            208 => "Object not found. Verify the table or view name.",
            207 => "Invalid column name.",
            547 => "Operation violates a foreign key or check constraint.",
            2601 or 2627 => "Duplicate key violation. A record with this key already exists.",
            515 => "Cannot insert NULL into a required column.",
            8152 => "Value too long for the target column.",
            1205 => "Transaction was deadlocked and has been rolled back.",
            3960 => "Snapshot isolation transaction aborted due to update conflict.",
            15247 => "Caller does not have permission to perform this action.",
            15151 => "Cannot find the specified object.",
            _ => $"SQL Server error {ex.Number}. Check server logs for details."
        };
    }
}
