using SqlServer.Profiler.Mcp.Utilities;
using Xunit;

namespace SqlServer.Profiler.Mcp.Tests.Utilities;

public class ConnectionStringResolverTests : IDisposable
{
    private const string EnvVar = "SQL_SENTINEL_CONNECTION_STRING";
    private readonly string? _originalEnvValue;

    public ConnectionStringResolverTests()
    {
        _originalEnvValue = Environment.GetEnvironmentVariable(EnvVar);
        // Clear env var for clean test state
        Environment.SetEnvironmentVariable(EnvVar, null);
    }

    public void Dispose()
    {
        // Restore original env var
        Environment.SetEnvironmentVariable(EnvVar, _originalEnvValue);
    }

    [Fact]
    public void Resolve_WithExplicitConnectionString_ReturnsIt()
    {
        var connStr = "Server=localhost;Database=test;Integrated Security=true";
        var result = ConnectionStringResolver.Resolve(connStr);
        Assert.Equal(connStr, result);
    }

    [Fact]
    public void Resolve_WithNullExplicit_FallsBackToEnvVar()
    {
        var envConnStr = "Server=envhost;Database=envdb;Integrated Security=true";
        Environment.SetEnvironmentVariable(EnvVar, envConnStr);

        var result = ConnectionStringResolver.Resolve(null);
        Assert.Equal(envConnStr, result);
    }

    [Fact]
    public void Resolve_WithEmptyExplicit_FallsBackToEnvVar()
    {
        var envConnStr = "Server=envhost;Database=envdb;Integrated Security=true";
        Environment.SetEnvironmentVariable(EnvVar, envConnStr);

        var result = ConnectionStringResolver.Resolve("");
        Assert.Equal(envConnStr, result);
    }

    [Fact]
    public void Resolve_WithWhitespaceExplicit_FallsBackToEnvVar()
    {
        var envConnStr = "Server=envhost;Database=envdb;Integrated Security=true";
        Environment.SetEnvironmentVariable(EnvVar, envConnStr);

        var result = ConnectionStringResolver.Resolve("   ");
        Assert.Equal(envConnStr, result);
    }

    [Fact]
    public void Resolve_WithNoExplicitAndNoEnvVar_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectionStringResolver.Resolve(null));
        Assert.Contains("SQL_SENTINEL_CONNECTION_STRING", ex.Message);
    }

    [Fact]
    public void Resolve_WithEmptyExplicitAndEmptyEnvVar_ThrowsArgumentException()
    {
        Environment.SetEnvironmentVariable(EnvVar, "");
        Assert.Throws<ArgumentException>(() => ConnectionStringResolver.Resolve(""));
    }
}
