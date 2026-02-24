using SqlServer.Profiler.Mcp.Utilities;
using Xunit;

namespace SqlServer.Profiler.Mcp.Tests.Utilities;

public class SqlInputValidatorTests
{
    // ── EscapeSqlString ─────────────────────────────────────────

    [Fact]
    public void EscapeSqlString_Null_ReturnsNull()
    {
        Assert.Null(SqlInputValidator.EscapeSqlString(null!));
    }

    [Fact]
    public void EscapeSqlString_Empty_ReturnsEmpty()
    {
        Assert.Equal("", SqlInputValidator.EscapeSqlString(""));
    }

    [Fact]
    public void EscapeSqlString_NoQuotes_ReturnsSameString()
    {
        Assert.Equal("hello world", SqlInputValidator.EscapeSqlString("hello world"));
    }

    [Fact]
    public void EscapeSqlString_SingleQuote_Doubled()
    {
        Assert.Equal("it''s", SqlInputValidator.EscapeSqlString("it's"));
    }

    [Fact]
    public void EscapeSqlString_MultipleQuotes_AllDoubled()
    {
        Assert.Equal("it''s a ''test''", SqlInputValidator.EscapeSqlString("it's a 'test'"));
    }

    // ── EscapeSqlLikePattern ────────────────────────────────────

    [Fact]
    public void EscapeSqlLikePattern_Null_ReturnsNull()
    {
        Assert.Null(SqlInputValidator.EscapeSqlLikePattern(null!));
    }

    [Fact]
    public void EscapeSqlLikePattern_Empty_ReturnsEmpty()
    {
        Assert.Equal("", SqlInputValidator.EscapeSqlLikePattern(""));
    }

    [Theory]
    [InlineData("%", "[%]")]
    [InlineData("_", "[_]")]
    [InlineData("[", "[[]")]
    public void EscapeSqlLikePattern_IndividualWildcards_Escaped(string input, string expected)
    {
        Assert.Equal(expected, SqlInputValidator.EscapeSqlLikePattern(input));
    }

    [Fact]
    public void EscapeSqlLikePattern_CombinedWildcardsAndQuote()
    {
        // Input: [%_'  =>  [ becomes [[]  then % becomes [%]  then _ becomes [_]  then ' becomes ''
        Assert.Equal("[[][%][_]''", SqlInputValidator.EscapeSqlLikePattern("[%_'"));
    }

    [Fact]
    public void EscapeSqlLikePattern_SingleQuote_Doubled()
    {
        Assert.Equal("O''Brien", SqlInputValidator.EscapeSqlLikePattern("O'Brien"));
    }

    // ── EscapeBrackets ──────────────────────────────────────────

    [Fact]
    public void EscapeBrackets_Null_ReturnsNull()
    {
        Assert.Null(SqlInputValidator.EscapeBrackets(null!));
    }

    [Fact]
    public void EscapeBrackets_Empty_ReturnsEmpty()
    {
        Assert.Equal("", SqlInputValidator.EscapeBrackets(""));
    }

    [Fact]
    public void EscapeBrackets_ClosingBracket_Doubled()
    {
        Assert.Equal("col]]name", SqlInputValidator.EscapeBrackets("col]name"));
    }

    [Fact]
    public void EscapeBrackets_MultipleBrackets_AllDoubled()
    {
        Assert.Equal("a]]b]]c", SqlInputValidator.EscapeBrackets("a]b]c"));
    }

    // ── IsValidSessionName ──────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("my_session", true)]
    [InlineData("session-1", true)]
    [InlineData("Session123", true)]
    [InlineData("bad session", false)]
    [InlineData("bad!name", false)]
    [InlineData("'; DROP TABLE--", false)]
    public void IsValidSessionName_VariousInputs(string? input, bool expected)
    {
        Assert.Equal(expected, SqlInputValidator.IsValidSessionName(input!));
    }

    // ── IsValidDatabaseName ─────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("MyDatabase", true)]
    [InlineData("db with space", true)]
    [InlineData("db@name", true)]
    [InlineData("db#name", true)]
    [InlineData("db$name", true)]
    [InlineData("bad;name", false)]
    public void IsValidDatabaseName_VariousInputs(string? input, bool expected)
    {
        Assert.Equal(expected, SqlInputValidator.IsValidDatabaseName(input!));
    }

    [Fact]
    public void IsValidDatabaseName_Over128Chars_ReturnsFalse()
    {
        var longName = new string('a', 129);
        Assert.False(SqlInputValidator.IsValidDatabaseName(longName));
    }

    [Fact]
    public void IsValidDatabaseName_Exactly128Chars_ReturnsTrue()
    {
        var name = new string('a', 128);
        Assert.True(SqlInputValidator.IsValidDatabaseName(name));
    }

    // ── IsValidLoginName ────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("sa", true)]
    [InlineData(@"DOMAIN\user", true)]
    [InlineData("user@domain.com", true)]
    [InlineData("user name", false)]
    public void IsValidLoginName_VariousInputs(string? input, bool expected)
    {
        Assert.Equal(expected, SqlInputValidator.IsValidLoginName(input!));
    }

    [Fact]
    public void IsValidLoginName_Over128Chars_ReturnsFalse()
    {
        var longName = new string('a', 129);
        Assert.False(SqlInputValidator.IsValidLoginName(longName));
    }

    // ── IsValidHostname ─────────────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("localhost", true)]
    [InlineData("server-01.domain.com", true)]
    [InlineData("bad host", false)]
    [InlineData("bad;host", false)]
    public void IsValidHostname_VariousInputs(string? input, bool expected)
    {
        Assert.Equal(expected, SqlInputValidator.IsValidHostname(input!));
    }

    [Fact]
    public void IsValidHostname_Over255Chars_ReturnsFalse()
    {
        var longHost = new string('a', 256);
        Assert.False(SqlInputValidator.IsValidHostname(longHost));
    }

    // ── IsValidApplicationName ──────────────────────────────────

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("My Application v2.0", true)]
    [InlineData("app with 'quote", false)]
    [InlineData("app;injection", false)]
    [InlineData("app--comment", false)]
    public void IsValidApplicationName_VariousInputs(string? input, bool expected)
    {
        Assert.Equal(expected, SqlInputValidator.IsValidApplicationName(input!));
    }

    [Fact]
    public void IsValidApplicationName_Over256Chars_ReturnsFalse()
    {
        var longApp = new string('A', 257);
        Assert.False(SqlInputValidator.IsValidApplicationName(longApp));
    }

    [Fact]
    public void IsValidApplicationName_Exactly256Chars_ReturnsTrue()
    {
        var app = new string('A', 256);
        Assert.True(SqlInputValidator.IsValidApplicationName(app));
    }

    // ── StartsWithKeyword ───────────────────────────────────────

    [Fact]
    public void StartsWithKeyword_Null_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.StartsWithKeyword(null!, "SELECT"));
    }

    [Fact]
    public void StartsWithKeyword_Empty_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.StartsWithKeyword("", "SELECT"));
    }

    [Fact]
    public void StartsWithKeyword_MatchingKeyword_ReturnsTrue()
    {
        Assert.True(SqlInputValidator.StartsWithKeyword("SELECT * FROM t", "SELECT"));
    }

    [Fact]
    public void StartsWithKeyword_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(SqlInputValidator.StartsWithKeyword("select * FROM t", "SELECT"));
    }

    [Fact]
    public void StartsWithKeyword_LeadingWhitespace_ReturnsTrue()
    {
        Assert.True(SqlInputValidator.StartsWithKeyword("   SELECT * FROM t", "SELECT"));
    }

    [Fact]
    public void StartsWithKeyword_NonMatching_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.StartsWithKeyword("INSERT INTO t", "SELECT"));
    }

    [Fact]
    public void StartsWithKeyword_MultipleAllowed_MatchesAny()
    {
        Assert.True(SqlInputValidator.StartsWithKeyword("INSERT INTO t", "SELECT", "INSERT"));
    }

    // ── ValidateStatement ───────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateStatement_EmptyOrNull_ReturnsInvalid(string? input)
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement(input!, "SELECT");
        Assert.False(isValid);
        Assert.Equal("SQL statement cannot be empty.", error);
    }

    [Fact]
    public void ValidateStatement_SelectContext_BlocksDrop()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement("SELECT 1; DROP TABLE x", "SELECT");
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateStatement_CreateTableContext_BlocksCreateLogin()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement(
            "CREATE TABLE t(id INT); CREATE LOGIN evil WITH PASSWORD='x'", "CREATE TABLE");
        Assert.False(isValid);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateStatement_InsertContext_BlocksExec()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement("EXEC sp_help", "INSERT");
        Assert.False(isValid);
        Assert.Contains("EXEC", error!);
    }

    [Fact]
    public void ValidateStatement_UpdateContext_BlocksTruncate()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement("TRUNCATE TABLE t", "UPDATE");
        Assert.False(isValid);
        Assert.Contains("TRUNCATE", error!);
    }

    [Fact]
    public void ValidateStatement_DropTableContext_BlocksDropDatabase()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement("DROP DATABASE mydb", "DROP TABLE");
        Assert.False(isValid);
        Assert.Contains("DROP DATABASE", error!);
    }

    [Fact]
    public void ValidateStatement_WordBoundary_DroppedDoesNotMatchDrop()
    {
        // "DROPPED" contains "DROP" but should NOT trigger the deny list
        // because the word boundary check should see the 'P' is followed by 'P' (letter).
        var (isValid, _) = SqlInputValidator.ValidateStatement(
            "SELECT DROPPED FROM t", "SELECT", ["DROP"]);
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateStatement_PrefixPattern_xp_Blocked()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement(
            "SELECT * FROM t WHERE xp_cmdshell='test'", "SELECT");
        Assert.False(isValid);
        Assert.Contains("xp_", error!);
    }

    [Fact]
    public void ValidateStatement_PrefixPattern_sp_OA_Blocked()
    {
        // sp_OA in the deny list doesn't end with '_', so exact word match required.
        // But EXEC sp_OA triggers the EXEC deny pattern instead.
        // Test that sp_OA as a standalone word is blocked:
        var (isValid, error) = SqlInputValidator.ValidateStatement(
            "SELECT 1 WHERE EXISTS(SELECT 1 FROM sp_OA)", "SELECT");
        Assert.False(isValid);
        Assert.NotNull(error);
        Assert.Contains("blocked keyword", error);
    }

    [Fact]
    public void ValidateStatement_MultiWordPattern_CreateLogin()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement(
            "CREATE LOGIN hacker WITH PASSWORD='pw'", "CREATE TABLE");
        Assert.False(isValid);
        Assert.Contains("CREATE LOGIN", error!);
    }

    [Fact]
    public void ValidateStatement_CleanSelect_ReturnsValid()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement(
            "SELECT Id, Name FROM Customers WHERE Active = 1", "SELECT");
        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateStatement_StatementSeparator_Blocked()
    {
        var (isValid, error) = SqlInputValidator.ValidateStatement(
            "SELECT 1; SELECT 2", "SELECT");
        Assert.False(isValid);
        Assert.Contains("separator", error!);
    }

    // ── ContainsStatementSeparator ──────────────────────────────

    [Fact]
    public void ContainsStatementSeparator_NoSeparator_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.ContainsStatementSeparator("SELECT * FROM t"));
    }

    [Fact]
    public void ContainsStatementSeparator_SemicolonMidStatement_ReturnsTrue()
    {
        Assert.True(SqlInputValidator.ContainsStatementSeparator("SELECT 1; SELECT 2"));
    }

    [Fact]
    public void ContainsStatementSeparator_TrailingSemicolon_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.ContainsStatementSeparator("SELECT 1;"));
    }

    [Fact]
    public void ContainsStatementSeparator_TrailingSemicolonWithWhitespace_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.ContainsStatementSeparator("SELECT 1;   "));
    }

    [Fact]
    public void ContainsStatementSeparator_SemicolonInsideStringLiteral_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.ContainsStatementSeparator("SELECT 'hello;world'"));
    }

    [Fact]
    public void ContainsStatementSeparator_GoOnOwnLine_ReturnsTrue()
    {
        Assert.True(SqlInputValidator.ContainsStatementSeparator("SELECT 1\nGO\nSELECT 2"));
    }

    [Fact]
    public void ContainsStatementSeparator_GoCaseInsensitive_ReturnsTrue()
    {
        Assert.True(SqlInputValidator.ContainsStatementSeparator("SELECT 1\ngo\nSELECT 2"));
    }

    [Fact]
    public void ContainsStatementSeparator_GoAsPartOfWord_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.ContainsStatementSeparator("SELECT CATEGORY FROM t"));
    }

    [Fact]
    public void ContainsStatementSeparator_EscapedQuotesInString_ReturnsFalse()
    {
        // The string literal contains escaped quotes: 'it''s ; fine'
        Assert.False(SqlInputValidator.ContainsStatementSeparator("SELECT 'it''s ; fine'"));
    }

    [Fact]
    public void ContainsStatementSeparator_NullInput_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.ContainsStatementSeparator(null!));
    }

    [Fact]
    public void ContainsStatementSeparator_EmptyInput_ReturnsFalse()
    {
        Assert.False(SqlInputValidator.ContainsStatementSeparator(""));
    }

    // ── GetDenyList ─────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT")]
    [InlineData("CREATE TABLE")]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DROP TABLE")]
    public void GetDenyList_ValidContext_ReturnsNonEmpty(string context)
    {
        var list = SqlInputValidator.GetDenyList(context);
        Assert.NotEmpty(list);
    }

    [Fact]
    public void GetDenyList_UnknownContext_ReturnsEmpty()
    {
        var list = SqlInputValidator.GetDenyList("MERGE");
        Assert.Empty(list);
    }

    [Fact]
    public void GetDenyList_CaseInsensitive_WorksWithLowercase()
    {
        // GetDenyList calls ToUpperInvariant, so lowercase input should work
        var list = SqlInputValidator.GetDenyList("select");
        Assert.NotEmpty(list);
    }
}
