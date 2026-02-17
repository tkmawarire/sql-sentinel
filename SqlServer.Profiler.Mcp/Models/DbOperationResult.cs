namespace SqlServer.Profiler.Mcp.Models;

/// <summary>
/// Represents the result of a database operation, including success status, error message, number of rows affected, and any returned data.
/// </summary>
public class DbOperationResult(bool success, string? error = null, int? rowsAffected = null, object? data = null)
{
    public bool Success { get; } = success;
    public string? Error { get; } = error;
    public int? RowsAffected { get; } = rowsAffected;
    public object? Data { get; } = data;
}
