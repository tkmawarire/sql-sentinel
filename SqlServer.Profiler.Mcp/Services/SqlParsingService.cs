using System.Text.RegularExpressions;
using SqlServer.Profiler.Mcp.Models;

namespace SqlServer.Profiler.Mcp.Services;

/// <summary>
/// Service for parsing SQL statements to extract table references, stored procedure calls, and operation types.
/// </summary>
public interface ISqlParsingService
{
    /// <summary>
    /// Parses a SQL statement and returns all extracted information in a single result.
    /// </summary>
    SqlParseResult Parse(string sqlText);

    /// <summary>
    /// Extracts all table names referenced in a SQL statement, deduplicated and normalized.
    /// Temp tables (#, ##) and table variables (@) are excluded.
    /// </summary>
    List<string> ExtractTableReferences(string sqlText);

    /// <summary>
    /// Extracts all stored procedure names from EXEC/EXECUTE calls in a SQL statement.
    /// </summary>
    List<string> ExtractStoredProcedures(string sqlText);

    /// <summary>
    /// Classifies the primary operation of a SQL statement (SELECT, INSERT, UPDATE, DELETE, EXEC, MERGE, DDL, UNKNOWN).
    /// Leading whitespace and comments are stripped before classification.
    /// </summary>
    string ClassifyOperation(string sqlText);
}

/// <summary>
/// Parses SQL statements using compiled regular expressions to extract structural information.
/// </summary>
public partial class SqlParsingService : ISqlParsingService
{
    /// <inheritdoc />
    public SqlParseResult Parse(string sqlText)
    {
        var tables = ExtractTableReferences(sqlText);
        var procs = ExtractStoredProcedures(sqlText);
        var operation = ClassifyOperation(sqlText);
        return new SqlParseResult(tables, procs, operation);
    }

    /// <inheritdoc />
    public List<string> ExtractTableReferences(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void TryAdd(string raw)
        {
            // Normalize bracket-quoted identifiers: [dbo].[Table] -> dbo.Table
            var normalized = BracketRegex().Replace(raw, "$1");

            // Skip temp tables and table variables
            if (normalized.StartsWith('#') || normalized.StartsWith('@'))
                return;

            if (seen.Add(normalized))
                result.Add(normalized);
        }

        foreach (Match m in FromClauseRegex().Matches(sqlText))
            TryAdd(m.Groups[1].Value);

        foreach (Match m in JoinClauseRegex().Matches(sqlText))
            TryAdd(m.Groups[1].Value);

        foreach (Match m in InsertIntoRegex().Matches(sqlText))
            TryAdd(m.Groups[1].Value);

        foreach (Match m in UpdateTableRegex().Matches(sqlText))
            TryAdd(m.Groups[1].Value);

        foreach (Match m in DeleteFromRegex().Matches(sqlText))
            TryAdd(m.Groups[1].Value);

        foreach (Match m in MergeIntoRegex().Matches(sqlText))
            TryAdd(m.Groups[1].Value);

        return result;
    }

    /// <inheritdoc />
    public List<string> ExtractStoredProcedures(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (Match m in ExecProcRegex().Matches(sqlText))
        {
            var raw = m.Groups[1].Value;
            // Normalize brackets
            var normalized = BracketRegex().Replace(raw, "$1");

            if (seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }

    /// <inheritdoc />
    public string ClassifyOperation(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
            return "UNKNOWN";

        // Strip leading whitespace
        var trimmed = sqlText.TrimStart();

        // Strip leading single-line comments (-- ...)
        trimmed = LeadingLineCommentRegex().Replace(trimmed, "").TrimStart();

        // Strip leading block comments (/* ... */)
        trimmed = LeadingBlockCommentRegex().Replace(trimmed, "").TrimStart();

        // Extract first keyword
        var match = FirstKeywordRegex().Match(trimmed);
        if (!match.Success)
            return "UNKNOWN";

        var keyword = match.Value.ToUpperInvariant();

        return keyword switch
        {
            "SELECT" => "SELECT",
            "INSERT" => "INSERT",
            "UPDATE" => "UPDATE",
            "DELETE" => "DELETE",
            "EXEC" or "EXECUTE" => "EXEC",
            "MERGE" => "MERGE",
            "CREATE" or "ALTER" or "DROP" or "TRUNCATE" => "DDL",
            _ => "UNKNOWN"
        };
    }

    // -----------------------------------------------------------------------
    // Compiled regex patterns
    // -----------------------------------------------------------------------

    /// <summary>Matches a bracket-quoted identifier segment, e.g. [dbo] -> dbo.</summary>
    [GeneratedRegex(@"\[(\w+)\]")]
    private static partial Regex BracketRegex();

    /// <summary>Matches table name after FROM keyword.</summary>
    [GeneratedRegex(@"\bFROM\s+(\[?[\w.]+\]?(?:\.\[?[\w]+\]?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex FromClauseRegex();

    /// <summary>Matches table name after JOIN keyword.</summary>
    [GeneratedRegex(@"\bJOIN\s+(\[?[\w.]+\]?(?:\.\[?[\w]+\]?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex JoinClauseRegex();

    /// <summary>Matches table name after INSERT INTO.</summary>
    [GeneratedRegex(@"\bINSERT\s+INTO\s+(\[?[\w.]+\]?(?:\.\[?[\w]+\]?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex InsertIntoRegex();

    /// <summary>Matches table name after UPDATE keyword.</summary>
    [GeneratedRegex(@"\bUPDATE\s+(\[?[\w.]+\]?(?:\.\[?[\w]+\]?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateTableRegex();

    /// <summary>Matches table name after DELETE (optional FROM).</summary>
    [GeneratedRegex(@"\bDELETE\s+(?:FROM\s+)?(\[?[\w.]+\]?(?:\.\[?[\w]+\]?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteFromRegex();

    /// <summary>Matches table name after MERGE (optional INTO).</summary>
    [GeneratedRegex(@"\bMERGE\s+(?:INTO\s+)?(\[?[\w.]+\]?(?:\.\[?[\w]+\]?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex MergeIntoRegex();

    /// <summary>
    /// Matches stored procedure name after EXEC/EXECUTE.
    /// Uses a negative lookahead to exclude EXEC( expressions (dynamic SQL).
    /// </summary>
    [GeneratedRegex(@"\b(?:EXEC|EXECUTE)\s+(?!\()(\[?[\w.]+\]?(?:\.\[?[\w]+\]?)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecProcRegex();

    /// <summary>Strips one or more leading single-line SQL comments.</summary>
    [GeneratedRegex(@"^(--[^\r\n]*[\r\n]*)+", RegexOptions.Multiline)]
    private static partial Regex LeadingLineCommentRegex();

    /// <summary>Strips one or more leading block SQL comments.</summary>
    [GeneratedRegex(@"^(/\*.*?\*/\s*)+", RegexOptions.Singleline)]
    private static partial Regex LeadingBlockCommentRegex();

    /// <summary>Extracts the first alphabetic keyword from SQL text.</summary>
    [GeneratedRegex(@"^\s*([A-Za-z]+)")]
    private static partial Regex FirstKeywordRegex();
}
