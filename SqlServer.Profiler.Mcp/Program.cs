using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SqlServer.Profiler.Mcp.Services;
using SqlServer.Profiler.Mcp.Tools;

namespace SqlServer.Profiler.Mcp;

/// <summary>
/// SQL Server Profiler MCP Server
/// 
/// A production-ready MCP server for SQL Server query profiling using Extended Events.
/// Uses Microsoft.Data.SqlClient for native connectivity - NO ODBC DRIVERS REQUIRED.
/// 
/// Features:
/// - Extended Events based (modern, low overhead, not deprecated)
/// - Session lifecycle management
/// - Rich filtering (app, database, user, duration, text patterns)
/// - AI-optimized output with query fingerprinting
/// - Production-safe with noise filtering
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        
        // Configure logging to stderr (stdout reserved for MCP protocol)
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => 
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Register services
        builder.Services.AddSingleton<IProfilerService, ProfilerService>();
        builder.Services.AddSingleton<IQueryFingerprintService, QueryFingerprintService>();
        builder.Services.AddSingleton<SessionConfigStore>();

        // Register MCP Server
        builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "sqlserver-profiler-mcp",
                Version = "1.0.0"
            };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(Program).Assembly);

        var app = builder.Build();
        await app.RunAsync();
    }
}
