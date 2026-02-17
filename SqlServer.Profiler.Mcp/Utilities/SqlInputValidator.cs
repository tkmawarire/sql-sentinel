using System.Text.RegularExpressions;

namespace SqlServer.Profiler.Mcp.Utilities;

/// <summary>
/// Provides input validation and escaping for SQL Server identifiers and predicates.
/// </summary>
public static partial class SqlInputValidator
{
    /// <summary>
    /// Escapes single quotes in a string for use in SQL string literals.
    /// </summary>
    public static string EscapeSqlString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Replace("'", "''");
    }

    /// <summary>
    /// Escapes wildcards and single quotes for use in SQL LIKE patterns.
    /// </summary>
    public static string EscapeSqlLikePattern(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("_", "[_]")
            .Replace("'", "''");
    }

    /// <summary>
    /// Escapes brackets for use in SQL Server bracket-delimited identifiers.
    /// </summary>
    public static string EscapeBrackets(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Replace("]", "]]");
    }

    /// <summary>
    /// Validates that a session name contains only safe characters (alphanumeric, underscore, hyphen).
    /// </summary>
    public static bool IsValidSessionName(string sessionName)
    {
        if (string.IsNullOrWhiteSpace(sessionName))
            return false;

        return ValidSessionNameRegex().IsMatch(sessionName);
    }

    /// <summary>
    /// Validates that a database name contains only safe characters. Max 128 characters.
    /// </summary>
    public static bool IsValidDatabaseName(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
            return false;

        return databaseName.Length <= 128 && ValidDatabaseNameRegex().IsMatch(databaseName);
    }

    /// <summary>
    /// Validates that a login name contains only safe characters (supports domain\user format). Max 128 characters.
    /// </summary>
    public static bool IsValidLoginName(string loginName)
    {
        if (string.IsNullOrWhiteSpace(loginName) || loginName.Length > 128)
            return false;

        return ValidLoginNameRegex().IsMatch(loginName);
    }

    /// <summary>
    /// Validates that a hostname contains only safe characters. Max 255 characters.
    /// </summary>
    public static bool IsValidHostname(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return false;

        return hostname.Length <= 255 && ValidHostnameRegex().IsMatch(hostname);
    }

    /// <summary>
    /// Validates that an application name does not contain SQL injection patterns. Max 256 characters.
    /// </summary>
    public static bool IsValidApplicationName(string applicationName)
    {
        if (string.IsNullOrWhiteSpace(applicationName))
            return false;

        return applicationName.Length <= 256 && !ContainsSqlInjectionPatterns().IsMatch(applicationName);
    }

    /// <summary>
    /// Validates that a SQL statement starts with one of the expected keywords.
    /// </summary>
    public static bool StartsWithKeyword(string sql, params string[] allowedKeywords)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var trimmed = sql.TrimStart();
        return allowedKeywords.Any(k =>
            trimmed.StartsWith(k, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"^[\w\-]+\z", RegexOptions.Compiled)]
    private static partial Regex ValidSessionNameRegex();

    [GeneratedRegex(@"^[\w\s\-@#$]+\z", RegexOptions.Compiled)]
    private static partial Regex ValidDatabaseNameRegex();

    [GeneratedRegex(@"^[\w\-\\@\.]+\z", RegexOptions.Compiled)]
    private static partial Regex ValidLoginNameRegex();

    [GeneratedRegex(@"^[\w\-\.]+\z", RegexOptions.Compiled)]
    private static partial Regex ValidHostnameRegex();

    [GeneratedRegex(@"('|;|--)", RegexOptions.Compiled)]
    private static partial Regex ContainsSqlInjectionPatterns();
}
