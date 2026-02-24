using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Moq;
using SqlServer.Profiler.Mcp.Utilities;
using Xunit;

namespace SqlServer.Profiler.Mcp.Tests.Utilities;

public class ErrorSanitizerTests
{
    // ──────────────────────────────────────────────
    // 1. ArgumentException — message passes through
    // ──────────────────────────────────────────────

    [Fact]
    public void Sanitize_ArgumentException_ReturnsOriginalMessage()
    {
        var ex = new ArgumentException("Table name is required.");
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Table name is required.", result);
    }

    [Fact]
    public void Sanitize_ArgumentNullException_ReturnsOriginalMessage()
    {
        var ex = new ArgumentNullException("connectionString", "Connection string cannot be null.");
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal(ex.Message, result);
    }

    // ──────────────────────────────────────────────
    // 2. SqlException error numbers
    // ──────────────────────────────────────────────

    [Fact]
    public void Sanitize_SqlException_TimeoutNumber_ReturnsTimeoutMessage()
    {
        var ex = CreateSqlException(-2);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("SQL Server operation timed out.", result);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(53)]
    public void Sanitize_SqlException_ConnectionError_ReturnsConnectionMessage(int errorNumber)
    {
        var ex = CreateSqlException(errorNumber);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Cannot connect to SQL Server. Verify the server address and network connectivity.", result);
    }

    [Fact]
    public void Sanitize_SqlException_DatabaseAccessError_ReturnsDatabaseMessage()
    {
        var ex = CreateSqlException(4060);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Cannot access the specified database. Verify the database name and permissions.", result);
    }

    [Fact]
    public void Sanitize_SqlException_LoginFailed_ReturnsLoginMessage()
    {
        var ex = CreateSqlException(18456);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Login failed. Verify credentials.", result);
    }

    [Theory]
    [InlineData(229)]
    [InlineData(230)]
    public void Sanitize_SqlException_PermissionError_ReturnsPermissionMessage(int errorNumber)
    {
        var ex = CreateSqlException(errorNumber);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Insufficient permissions for this operation.", result);
    }

    [Fact]
    public void Sanitize_SqlException_ObjectNotFound_ReturnsObjectNotFoundMessage()
    {
        var ex = CreateSqlException(208);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Object not found. Verify the table or view name.", result);
    }

    [Fact]
    public void Sanitize_SqlException_InvalidColumn_ReturnsInvalidColumnMessage()
    {
        var ex = CreateSqlException(207);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Invalid column name.", result);
    }

    [Fact]
    public void Sanitize_SqlException_ConstraintViolation_ReturnsConstraintMessage()
    {
        var ex = CreateSqlException(547);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Operation violates a foreign key or check constraint.", result);
    }

    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    public void Sanitize_SqlException_DuplicateKey_ReturnsDuplicateKeyMessage(int errorNumber)
    {
        var ex = CreateSqlException(errorNumber);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Duplicate key violation. A record with this key already exists.", result);
    }

    [Fact]
    public void Sanitize_SqlException_NullInsert_ReturnsNullInsertMessage()
    {
        var ex = CreateSqlException(515);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Cannot insert NULL into a required column.", result);
    }

    [Fact]
    public void Sanitize_SqlException_ValueTooLong_ReturnsValueTooLongMessage()
    {
        var ex = CreateSqlException(8152);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Value too long for the target column.", result);
    }

    [Fact]
    public void Sanitize_SqlException_Deadlock_ReturnsDeadlockMessage()
    {
        var ex = CreateSqlException(1205);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Transaction was deadlocked and has been rolled back.", result);
    }

    [Fact]
    public void Sanitize_SqlException_SnapshotIsolation_ReturnsSnapshotMessage()
    {
        var ex = CreateSqlException(3960);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Snapshot isolation transaction aborted due to update conflict.", result);
    }

    [Fact]
    public void Sanitize_SqlException_NoPermission_ReturnsNoPermissionMessage()
    {
        var ex = CreateSqlException(15247);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Caller does not have permission to perform this action.", result);
    }

    [Fact]
    public void Sanitize_SqlException_CannotFind_ReturnsCannotFindMessage()
    {
        var ex = CreateSqlException(15151);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Cannot find the specified object.", result);
    }

    [Fact]
    public void Sanitize_SqlException_UnknownNumber_ReturnsFallbackWithNumber()
    {
        var ex = CreateSqlException(99999);
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("SQL Server error 99999. Check server logs for details.", result);
    }

    // ──────────────────────────────────────────────
    // 3. Other exception types
    // ──────────────────────────────────────────────

    [Fact]
    public void Sanitize_InvalidOperationException_ReturnsOperationFailedMessage()
    {
        var ex = new InvalidOperationException("Some internal detail.");
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Operation failed. Check session state and connection.", result);
    }

    [Fact]
    public void Sanitize_TimeoutException_ReturnsTimedOutMessage()
    {
        var ex = new TimeoutException("Connection timed out after 30s.");
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Operation timed out.", result);
    }

    [Fact]
    public void Sanitize_OperationCanceledException_ReturnsCancelledMessage()
    {
        var ex = new OperationCanceledException("Token was cancelled.");
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("Operation was cancelled.", result);
    }

    [Fact]
    public void Sanitize_GenericException_ReturnsUnexpectedErrorMessage()
    {
        var ex = new Exception("Something broke internally.");
        var result = ErrorSanitizer.Sanitize(ex);
        Assert.Equal("An unexpected error occurred. Check server logs for details.", result);
    }

    // ──────────────────────────────────────────────
    // 4. Logger behavior
    // ──────────────────────────────────────────────

    [Fact]
    public void Sanitize_NullLogger_DoesNotThrow()
    {
        var ex = new Exception("test");
        var exception = Record.Exception(() => ErrorSanitizer.Sanitize(ex, logger: null));
        Assert.Null(exception);
    }

    [Fact]
    public void Sanitize_WithLogger_LogsException()
    {
        var mockLogger = new Mock<ILogger>();
        var ex = new InvalidOperationException("internal detail");

        ErrorSanitizer.Sanitize(ex, logger: mockLogger.Object, context: "TestContext");

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("TestContext")),
                ex,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Sanitize_WithLogger_NoContext_LogsUnknownContext()
    {
        var mockLogger = new Mock<ILogger>();
        var ex = new Exception("boom");

        ErrorSanitizer.Sanitize(ex, logger: mockLogger.Object);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("unknown")),
                ex,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ──────────────────────────────────────────────
    // Helper: construct SqlException via reflection
    // ──────────────────────────────────────────────

    private static SqlException CreateSqlException(int number)
    {
        var collectionType = typeof(SqlErrorCollection);
        var collection = (SqlErrorCollection)Activator.CreateInstance(collectionType, nonPublic: true)!;

        var errorType = typeof(SqlError);
        var ctors = errorType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
        var ctor = ctors.OrderByDescending(c => c.GetParameters().Length).First();
        var errorParams = ctor.GetParameters();

        // Build parameter array matching the longest constructor signature.
        // Known signatures vary across Microsoft.Data.SqlClient versions;
        // the longest one is used to maximize compatibility.
        var args = new object?[errorParams.Length];
        for (int i = 0; i < errorParams.Length; i++)
        {
            var p = errorParams[i];
            if (p.Name == "infoNumber" || p.Name == "errorNumber")
                args[i] = number;
            else if (p.ParameterType == typeof(byte))
                args[i] = (byte)0;
            else if (p.ParameterType == typeof(int))
                args[i] = 0;
            else if (p.ParameterType == typeof(uint))
                args[i] = (uint)0;
            else if (p.ParameterType == typeof(string))
                args[i] = "test";
            else if (p.ParameterType == typeof(Exception))
                args[i] = null;
            else
                args[i] = null;
        }

        var error = ctor.Invoke(args);

        var addMethod = collectionType.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!;
        addMethod.Invoke(collection, new[] { error });

        var sqlExType = typeof(SqlException);
        var sqlExCtors = sqlExType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
        var sqlExCtor = sqlExCtors.OrderByDescending(c => c.GetParameters().Length).First();
        var sqlExParams = sqlExCtor.GetParameters();

        var sqlExArgs = new object?[sqlExParams.Length];
        for (int i = 0; i < sqlExParams.Length; i++)
        {
            var p = sqlExParams[i];
            if (p.ParameterType == typeof(string))
                sqlExArgs[i] = "test message";
            else if (p.ParameterType == typeof(SqlErrorCollection))
                sqlExArgs[i] = collection;
            else if (p.ParameterType == typeof(Exception))
                sqlExArgs[i] = null;
            else if (p.ParameterType == typeof(Guid))
                sqlExArgs[i] = Guid.Empty;
            else
                sqlExArgs[i] = null;
        }

        return (SqlException)sqlExCtor.Invoke(sqlExArgs);
    }
}
