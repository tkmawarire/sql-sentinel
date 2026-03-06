using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Tests.Services;

public class SqlParsingServiceTests
{
    private readonly SqlParsingService _service = new();

    // -----------------------------------------------------------------------
    // ExtractTableReferences
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractTableReferences_SimpleSelectFrom_ReturnsSingleTable()
    {
        var tables = _service.ExtractTableReferences("SELECT * FROM Users");
        Assert.Single(tables);
        Assert.Equal("Users", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_SelectWithJoin_ReturnsBothTables()
    {
        var tables = _service.ExtractTableReferences("SELECT * FROM Orders o INNER JOIN Customers c ON o.CustomerId = c.Id");
        Assert.Contains("Orders", tables);
        Assert.Contains("Customers", tables);
        Assert.Equal(2, tables.Count);
    }

    [Fact]
    public void ExtractTableReferences_SelectWithMultipleJoins_ReturnsAllTables()
    {
        var sql = "SELECT * FROM Orders o JOIN Customers c ON o.CustomerId = c.Id LEFT JOIN Products p ON o.ProductId = p.Id";
        var tables = _service.ExtractTableReferences(sql);
        Assert.Contains("Orders", tables);
        Assert.Contains("Customers", tables);
        Assert.Contains("Products", tables);
        Assert.Equal(3, tables.Count);
    }

    [Fact]
    public void ExtractTableReferences_InsertInto_ReturnsTable()
    {
        var tables = _service.ExtractTableReferences("INSERT INTO Orders (CustomerId, Amount) VALUES (1, 99.99)");
        Assert.Single(tables);
        Assert.Equal("Orders", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_UpdateTable_ReturnsTable()
    {
        var tables = _service.ExtractTableReferences("UPDATE Users SET Name = 'Alice' WHERE Id = 1");
        Assert.Single(tables);
        Assert.Equal("Users", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_DeleteFrom_ReturnsTable()
    {
        var tables = _service.ExtractTableReferences("DELETE FROM Orders WHERE Id = 5");
        Assert.Single(tables);
        Assert.Equal("Orders", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_DeleteWithoutFrom_ReturnsTable()
    {
        var tables = _service.ExtractTableReferences("DELETE Orders WHERE Id = 5");
        Assert.Single(tables);
        Assert.Equal("Orders", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_MergeInto_ReturnsTable()
    {
        var sql = "MERGE INTO TargetTable AS t USING SourceTable AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.Name = s.Name;";
        var tables = _service.ExtractTableReferences(sql);
        Assert.Contains("TargetTable", tables);
    }

    [Fact]
    public void ExtractTableReferences_SchemaQualifiedTable_ReturnsSchemaAndTable()
    {
        var tables = _service.ExtractTableReferences("SELECT * FROM dbo.Users");
        Assert.Single(tables);
        Assert.Equal("dbo.Users", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_BracketQuotedTable_NormalizesBrackets()
    {
        var tables = _service.ExtractTableReferences("SELECT * FROM [dbo].[Users]");
        Assert.Single(tables);
        Assert.Equal("dbo.Users", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_MixedBracketAndNonBracket_NormalizedCorrectly()
    {
        var tables = _service.ExtractTableReferences("SELECT * FROM [dbo].Users");
        Assert.Single(tables);
        Assert.Equal("dbo.Users", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_CteWithSubsequentSelect_ReturnsBaseTable()
    {
        var sql = @"
            WITH CTE AS (SELECT Id FROM Orders WHERE Amount > 100)
            SELECT * FROM CTE";
        var tables = _service.ExtractTableReferences(sql);
        // Orders is extracted from the inner SELECT; CTE itself also appears in outer FROM
        Assert.Contains("Orders", tables);
    }

    [Fact]
    public void ExtractTableReferences_SubqueryInFrom_ExtractsInnerTable()
    {
        var sql = "SELECT * FROM (SELECT Id FROM Products WHERE Price > 10) sub";
        var tables = _service.ExtractTableReferences(sql);
        Assert.Contains("Products", tables);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractTableReferences_NullOrEmpty_ReturnsEmptyList(string? sql)
    {
        var tables = _service.ExtractTableReferences(sql!);
        Assert.Empty(tables);
    }

    [Fact]
    public void ExtractTableReferences_NoTablesFound_ReturnsEmptyList()
    {
        var tables = _service.ExtractTableReferences("SELECT GETDATE()");
        Assert.Empty(tables);
    }

    [Fact]
    public void ExtractTableReferences_CaseInsensitiveKeywords_ReturnsTable()
    {
        var tables = _service.ExtractTableReferences("select * from users");
        Assert.Single(tables);
        Assert.Equal("users", tables[0]);
    }

    [Fact]
    public void ExtractTableReferences_TempTable_IsExcluded()
    {
        var tables = _service.ExtractTableReferences("SELECT * FROM #TempResults");
        Assert.Empty(tables);
    }

    [Fact]
    public void ExtractTableReferences_GlobalTempTable_IsExcluded()
    {
        var tables = _service.ExtractTableReferences("SELECT * FROM ##GlobalTemp");
        Assert.Empty(tables);
    }

    [Fact]
    public void ExtractTableReferences_TableVariable_IsExcluded()
    {
        var tables = _service.ExtractTableReferences("SELECT * FROM @tableVar");
        Assert.Empty(tables);
    }

    [Fact]
    public void ExtractTableReferences_DuplicateTableReferences_ReturnedOnce()
    {
        var sql = "SELECT * FROM Orders o JOIN Orders o2 ON o.ParentId = o2.Id";
        var tables = _service.ExtractTableReferences(sql);
        Assert.Single(tables);
        Assert.Equal("Orders", tables[0]);
    }

    // -----------------------------------------------------------------------
    // ExtractStoredProcedures
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractStoredProcedures_SimpleExec_ReturnsProcName()
    {
        var procs = _service.ExtractStoredProcedures("EXEC sp_helptext 'Users'");
        Assert.Single(procs);
        Assert.Equal("sp_helptext", procs[0]);
    }

    [Fact]
    public void ExtractStoredProcedures_ExecuteWithSchema_ReturnsProcName()
    {
        var procs = _service.ExtractStoredProcedures("EXECUTE dbo.usp_GetOrders");
        Assert.Single(procs);
        Assert.Equal("dbo.usp_GetOrders", procs[0]);
    }

    [Fact]
    public void ExtractStoredProcedures_BracketQuotedProc_NormalizesBrackets()
    {
        var procs = _service.ExtractStoredProcedures("EXEC [dbo].[usp_GetUsers]");
        Assert.Single(procs);
        Assert.Equal("dbo.usp_GetUsers", procs[0]);
    }

    [Fact]
    public void ExtractStoredProcedures_SpExecutesql_IsDetected()
    {
        var procs = _service.ExtractStoredProcedures("EXEC sp_executesql N'SELECT 1'");
        Assert.Single(procs);
        Assert.Equal("sp_executesql", procs[0]);
    }

    [Fact]
    public void ExtractStoredProcedures_ExecWithParentheses_NotCaptured()
    {
        // EXEC('SELECT 1') is dynamic SQL, not a named SP
        var procs = _service.ExtractStoredProcedures("EXEC('SELECT 1')");
        Assert.Empty(procs);
    }

    [Fact]
    public void ExtractStoredProcedures_MultipleExecStatements_ReturnsAll()
    {
        var sql = "EXEC dbo.proc1; EXEC dbo.proc2";
        var procs = _service.ExtractStoredProcedures(sql);
        Assert.Contains("dbo.proc1", procs);
        Assert.Contains("dbo.proc2", procs);
        Assert.Equal(2, procs.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractStoredProcedures_NullOrEmpty_ReturnsEmptyList(string? sql)
    {
        var procs = _service.ExtractStoredProcedures(sql!);
        Assert.Empty(procs);
    }

    [Fact]
    public void ExtractStoredProcedures_CaseInsensitive_DetectsExecVariants()
    {
        var procs = _service.ExtractStoredProcedures("exec dbo.MyProc");
        Assert.Single(procs);
        Assert.Equal("dbo.MyProc", procs[0]);
    }

    // -----------------------------------------------------------------------
    // ClassifyOperation
    // -----------------------------------------------------------------------

    [Fact]
    public void ClassifyOperation_Select_ReturnsSelect()
    {
        Assert.Equal("SELECT", _service.ClassifyOperation("SELECT * FROM Users"));
    }

    [Fact]
    public void ClassifyOperation_Insert_ReturnsInsert()
    {
        Assert.Equal("INSERT", _service.ClassifyOperation("INSERT INTO Users VALUES (1)"));
    }

    [Fact]
    public void ClassifyOperation_Update_ReturnsUpdate()
    {
        Assert.Equal("UPDATE", _service.ClassifyOperation("UPDATE Users SET Name = 'X'"));
    }

    [Fact]
    public void ClassifyOperation_Delete_ReturnsDelete()
    {
        Assert.Equal("DELETE", _service.ClassifyOperation("DELETE FROM Users WHERE Id = 1"));
    }

    [Fact]
    public void ClassifyOperation_Exec_ReturnsExec()
    {
        Assert.Equal("EXEC", _service.ClassifyOperation("EXEC dbo.usp_DoWork"));
    }

    [Fact]
    public void ClassifyOperation_Execute_ReturnsExec()
    {
        Assert.Equal("EXEC", _service.ClassifyOperation("EXECUTE dbo.usp_DoWork"));
    }

    [Fact]
    public void ClassifyOperation_Merge_ReturnsMerge()
    {
        Assert.Equal("MERGE", _service.ClassifyOperation("MERGE INTO Target USING Source ON 1=1"));
    }

    [Fact]
    public void ClassifyOperation_CreateTable_ReturnsDdl()
    {
        Assert.Equal("DDL", _service.ClassifyOperation("CREATE TABLE Foo (Id INT)"));
    }

    [Fact]
    public void ClassifyOperation_AlterTable_ReturnsDdl()
    {
        Assert.Equal("DDL", _service.ClassifyOperation("ALTER TABLE Foo ADD Bar NVARCHAR(100)"));
    }

    [Fact]
    public void ClassifyOperation_DropTable_ReturnsDdl()
    {
        Assert.Equal("DDL", _service.ClassifyOperation("DROP TABLE Foo"));
    }

    [Fact]
    public void ClassifyOperation_TruncateTable_ReturnsDdl()
    {
        Assert.Equal("DDL", _service.ClassifyOperation("TRUNCATE TABLE Foo"));
    }

    [Fact]
    public void ClassifyOperation_LeadingWhitespace_IsHandled()
    {
        Assert.Equal("SELECT", _service.ClassifyOperation("   SELECT * FROM Users"));
    }

    [Fact]
    public void ClassifyOperation_LeadingSingleLineComment_IsStripped()
    {
        var sql = "-- this is a comment\nSELECT * FROM Users";
        Assert.Equal("SELECT", _service.ClassifyOperation(sql));
    }

    [Fact]
    public void ClassifyOperation_LeadingBlockComment_IsStripped()
    {
        var sql = "/* comment */ SELECT * FROM Users";
        Assert.Equal("SELECT", _service.ClassifyOperation(sql));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClassifyOperation_NullOrEmpty_ReturnsUnknown(string? sql)
    {
        Assert.Equal("UNKNOWN", _service.ClassifyOperation(sql!));
    }

    [Fact]
    public void ClassifyOperation_UnrecognizedKeyword_ReturnsUnknown()
    {
        Assert.Equal("UNKNOWN", _service.ClassifyOperation("WAITFOR DELAY '00:00:01'"));
    }

    // -----------------------------------------------------------------------
    // Parse (integration)
    // -----------------------------------------------------------------------

    [Fact]
    public void Parse_SimpleSelect_PopulatesAllFields()
    {
        var result = _service.Parse("SELECT * FROM dbo.Users");

        Assert.Equal("SELECT", result.OperationType);
        Assert.Contains("dbo.Users", result.Tables);
        Assert.Empty(result.StoredProcedures);
    }

    [Fact]
    public void Parse_ComplexSqlWithJoinAndExec_PopulatesAllFields()
    {
        var sql = @"
            EXEC dbo.usp_LogAccess;
            SELECT u.Id, o.Amount
            FROM dbo.Users u
            INNER JOIN dbo.Orders o ON u.Id = o.UserId
            WHERE u.IsActive = 1";

        var result = _service.Parse(sql);

        // First statement is EXEC so operation is EXEC
        Assert.Equal("EXEC", result.OperationType);
        Assert.Contains("dbo.usp_LogAccess", result.StoredProcedures);
        Assert.Contains("dbo.Users", result.Tables);
        Assert.Contains("dbo.Orders", result.Tables);
    }
}
