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
    public void Resolve_WithEnvVar_ReturnsIt()
    {
        var envConnStr = "Server=envhost;Database=envdb;Integrated Security=true";
        Environment.SetEnvironmentVariable(EnvVar, envConnStr);

        var result = ConnectionStringResolver.Resolve();
        Assert.Equal(envConnStr, result);
    }

    [Fact]
    public void Resolve_WithNoEnvVar_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => ConnectionStringResolver.Resolve());
        Assert.Contains("SQL_SENTINEL_CONNECTION_STRING", ex.Message);
    }

    [Fact]
    public void Resolve_WithEmptyEnvVar_ThrowsArgumentException()
    {
        Environment.SetEnvironmentVariable(EnvVar, "");
        Assert.Throws<ArgumentException>(() => ConnectionStringResolver.Resolve());
    }

    [Fact]
    public void Resolve_WithWhitespaceEnvVar_ThrowsArgumentException()
    {
        Environment.SetEnvironmentVariable(EnvVar, "   ");
        Assert.Throws<ArgumentException>(() => ConnectionStringResolver.Resolve());
    }
}
