using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlServer.Profiler.Mcp.Services;

/// <summary>
/// Service for generating query fingerprints by normalizing SQL queries.
/// </summary>
public interface IQueryFingerprintService
{
    string GenerateFingerprint(string sql);
}

/// <summary>
/// Generates query fingerprints by normalizing SQL text.
/// Normalizes literals, whitespace, and other variable parts to group similar queries.
/// </summary>
public partial class QueryFingerprintService : IQueryFingerprintService
{
    public string GenerateFingerprint(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "empty";

        var normalized = NormalizeSql(sql);
        return ComputeHash(normalized);
    }

    private static string NormalizeSql(string sql)
    {
        // Normalize to lowercase
        var result = sql.ToLowerInvariant();

        // Replace string literals with placeholder
        result = StringLiteralRegex().Replace(result, "'?'");

        // Replace unicode string literals
        result = UnicodeStringLiteralRegex().Replace(result, "N'?'");

        // Replace numeric literals (integers and decimals)
        result = NumericLiteralRegex().Replace(result, "?");

        // Replace GUID literals
        result = GuidLiteralRegex().Replace(result, "'?'");

        // Replace hex literals
        result = HexLiteralRegex().Replace(result, "?");

        // Normalize whitespace
        result = WhitespaceRegex().Replace(result, " ");

        // Trim
        result = result.Trim();

        return result;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        // Return first 16 chars of hex for brevity
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"N'(?:[^']|'')*'", RegexOptions.IgnoreCase)]
    private static partial Regex UnicodeStringLiteralRegex();

    [GeneratedRegex(@"\b\d+\.?\d*\b")]
    private static partial Regex NumericLiteralRegex();

    [GeneratedRegex(@"'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'", RegexOptions.IgnoreCase)]
    private static partial Regex GuidLiteralRegex();

    [GeneratedRegex(@"0x[0-9a-f]+", RegexOptions.IgnoreCase)]
    private static partial Regex HexLiteralRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
