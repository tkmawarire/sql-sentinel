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

    // ── Statement validation for DatabaseTools ─────────────────

    private static readonly string[] SelectDenyList =
    [
        "DROP", "DELETE", "TRUNCATE", "ALTER", "CREATE", "INSERT", "UPDATE",
        "EXEC", "EXECUTE", "xp_", "sp_OA", "OPENROWSET", "OPENDATASOURCE",
        "BULK INSERT", "SHUTDOWN", "RECONFIGURE"
    ];

    private static readonly string[] CreateTableDenyList =
    [
        "CREATE LOGIN", "CREATE USER", "CREATE DATABASE", "CREATE PROCEDURE",
        "CREATE FUNCTION", "CREATE TRIGGER", "EXEC", "EXECUTE",
        "xp_", "sp_OA", "OPENROWSET"
    ];

    private static readonly string[] InsertDenyList =
    [
        "DROP", "TRUNCATE", "ALTER", "CREATE", "EXEC", "EXECUTE",
        "xp_", "sp_OA", "OPENROWSET"
    ];

    private static readonly string[] UpdateDenyList =
    [
        "DROP", "TRUNCATE", "ALTER", "CREATE", "EXEC", "EXECUTE",
        "xp_", "sp_OA", "OPENROWSET"
    ];

    private static readonly string[] DropTableDenyList =
    [
        "DROP DATABASE", "DROP LOGIN", "DROP USER",
        "EXEC", "EXECUTE", "xp_", "sp_OA"
    ];

    /// <summary>
    /// Returns the appropriate deny list for a given SQL statement context.
    /// </summary>
    public static string[] GetDenyList(string context) => context.ToUpperInvariant() switch
    {
        "SELECT" => SelectDenyList,
        "CREATE TABLE" => CreateTableDenyList,
        "INSERT" => InsertDenyList,
        "UPDATE" => UpdateDenyList,
        "DROP TABLE" => DropTableDenyList,
        _ => []
    };

    /// <summary>
    /// Validates a SQL statement against a deny list of dangerous keywords.
    /// Returns (true, null) if valid, or (false, errorMessage) if blocked.
    /// </summary>
    public static (bool IsValid, string? Error) ValidateStatement(string sql, string context, string[]? denyPatterns = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return (false, "SQL statement cannot be empty.");

        denyPatterns ??= GetDenyList(context);

        // Check for statement separators (batch injection)
        if (ContainsStatementSeparator(sql))
            return (false, "Multiple SQL statements are not allowed. Remove ';' or 'GO' separators.");

        // Check deny list — case-insensitive word boundary matching
        var upper = sql.ToUpperInvariant();
        foreach (var pattern in denyPatterns)
        {
            var patternUpper = pattern.ToUpperInvariant();

            // For multi-word patterns (e.g., "CREATE LOGIN"), check directly
            if (patternUpper.Contains(' '))
            {
                if (upper.Contains(patternUpper))
                    return (false, $"Statement contains blocked keyword '{pattern}' in {context} context.");
            }
            else
            {
                // For single-word patterns, check with word boundary awareness
                var idx = 0;
                while ((idx = upper.IndexOf(patternUpper, idx, StringComparison.Ordinal)) >= 0)
                {
                    // Check if it's a standalone word (not part of a longer identifier)
                    var before = idx > 0 ? upper[idx - 1] : ' ';
                    var after = idx + patternUpper.Length < upper.Length ? upper[idx + patternUpper.Length] : ' ';

                    // For prefixes like "xp_" and "sp_OA", only check the before boundary
                    var isPrefix = patternUpper.EndsWith('_');
                    var isWordBefore = !char.IsLetterOrDigit(before) && before != '_';
                    var isWordAfter = isPrefix || (!char.IsLetterOrDigit(after) && after != '_');

                    if (isWordBefore && isWordAfter)
                        return (false, $"Statement contains blocked keyword '{pattern}' in {context} context.");

                    idx += patternUpper.Length;
                }
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Detects statement separators (';' outside string literals, 'GO' on its own line)
    /// that indicate multiple statements in a batch.
    /// </summary>
    public static bool ContainsStatementSeparator(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return false;

        // Check for ';' outside of string literals
        var inString = false;
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '\'')
            {
                // Handle escaped quotes ('')
                if (inString && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i++; // Skip escaped quote
                    continue;
                }
                inString = !inString;
            }
            else if (sql[i] == ';' && !inString)
            {
                // Allow trailing semicolons with only whitespace after
                var remaining = sql[(i + 1)..].Trim();
                if (remaining.Length > 0)
                    return true;
            }
        }

        // Check for 'GO' on its own line (batch separator)
        var lines = sql.Split('\n');
        foreach (var line in lines)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
