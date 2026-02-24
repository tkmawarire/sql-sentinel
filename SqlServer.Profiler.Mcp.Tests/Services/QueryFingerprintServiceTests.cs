using SqlServer.Profiler.Mcp.Services;
using Xunit;

namespace SqlServer.Profiler.Mcp.Tests.Services;

public class QueryFingerprintServiceTests
{
    private readonly QueryFingerprintService _service = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void GenerateFingerprint_NullOrWhitespace_ReturnsEmpty(string? sql)
    {
        var result = _service.GenerateFingerprint(sql!);
        Assert.Equal("empty", result);
    }

    [Fact]
    public void GenerateFingerprint_SameQueryDifferentStringLiterals_SameFingerprint()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Users WHERE Name = 'Alice'");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Users WHERE Name = 'Bob'");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_SameQueryDifferentNumericLiterals_SameFingerprint()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Orders WHERE Id = 123");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Orders WHERE Id = 456");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_SameQueryDifferentCase_SameFingerprint()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Users");
        var fp2 = _service.GenerateFingerprint("select * from users");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_SameQueryDifferentWhitespace_SameFingerprint()
    {
        var fp1 = _service.GenerateFingerprint("SELECT  *  FROM   Users");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Users");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_DifferentQueries_DifferentFingerprints()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Users");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Orders");
        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_UnicodeStringLiterals_Normalized()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Users WHERE Name = N'Alice'");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Users WHERE Name = N'Bob'");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_GuidLiterals_Normalized()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Users WHERE Id = '12345678-1234-1234-1234-123456789abc'");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Users WHERE Id = 'abcdefab-abcd-abcd-abcd-abcdefabcdef'");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_HexLiterals_Normalized()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Data WHERE Hash = 0xDEADBEEF");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Data WHERE Hash = 0xCAFEBABE");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_EscapedQuotesInStrings_HandledCorrectly()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Users WHERE Name = 'O''Brien'");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Users WHERE Name = 'Smith'");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_DecimalLiterals_Normalized()
    {
        var fp1 = _service.GenerateFingerprint("SELECT * FROM Products WHERE Price > 19.99");
        var fp2 = _service.GenerateFingerprint("SELECT * FROM Products WHERE Price > 42.50");
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void GenerateFingerprint_Returns16CharHex()
    {
        var fp = _service.GenerateFingerprint("SELECT 1");
        Assert.Equal(16, fp.Length);
        Assert.Matches("^[0-9a-f]{16}$", fp);
    }
}
