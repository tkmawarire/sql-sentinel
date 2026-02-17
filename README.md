<!-- mcp-name: io.github.tkmawarire/sql-sentinel -->
# SQL Sentinel MCP Server

[![NuGet](https://img.shields.io/nuget/v/Neofenyx.SqlSentinel.Mcp)](https://www.nuget.org/packages/Neofenyx.SqlSentinel.Mcp)
[![Docker](https://img.shields.io/badge/ghcr.io-sql--sentinel--mcp-blue)](https://ghcr.io/tkmawarire/sql-sentinel-mcp)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A production-ready MCP (Model Context Protocol) server for SQL Server monitoring and diagnostics using Extended Events. Built with .NET 9 and Microsoft.Data.SqlClient for **native SQL Server connectivity — no ODBC drivers required**.

## Features

- **Session Management** — Create, start, stop, drop, and list Extended Events sessions
- **Smart Filtering** — Filter by application, database, user, duration, host, and text patterns
- **Query Fingerprinting** — Normalize and group similar queries differing only in literal values
- **Sequence Analysis** — Trace execution order with timing gaps and cumulative duration
- **Deadlock Detection** — Capture and analyze XML deadlock reports with victim/process details
- **Blocking Analysis** — Monitor blocked process events with wait resource and SQL text
- **Wait Stats** — Query `sys.dm_os_wait_stats` directly, categorized by type (CPU, I/O, Lock, Memory, etc.)
- **Health Check** — Comprehensive server diagnostic: slow queries, deadlocks, blocking, wait stats, and insights
- **Real-Time Streaming** — Stream captured events for a specified duration
- **Production-Safe** — Auto-excludes noise (`sp_reset_connection`, `SET` statements, trace queries)
- **AI-Optimized** — Structured JSON output with optional Markdown formatting

## Requirements

- SQL Server 2012+ with Extended Events enabled (default)
- Required permissions:
  ```sql
  GRANT ALTER ANY EVENT SESSION TO [your_login];
  GRANT VIEW SERVER STATE TO [your_login];
  ```
- For blocked process detection:
  ```sql
  EXEC sp_configure 'show advanced options', 1;
  RECONFIGURE;
  EXEC sp_configure 'blocked process threshold', 5;
  RECONFIGURE;
  ```

## Installation

### Option 1: Docker (Recommended)

No .NET SDK required. Works on any system with Docker installed.

```bash
docker pull ghcr.io/tkmawarire/sql-sentinel-mcp:latest
```

#### Claude Desktop (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "sql-sentinel": {
      "command": "docker",
      "args": ["run", "-i", "--rm", "--network", "host",
               "ghcr.io/tkmawarire/sql-sentinel-mcp:latest"]
    }
  }
}
```

#### Claude Code

```bash
claude mcp add sql-sentinel -- docker run -i --rm --network host ghcr.io/tkmawarire/sql-sentinel-mcp:latest
```

> **Network access**: The `-i` flag is required for stdio transport. Use `--network host` so the container can reach SQL Server on your host machine. For remote SQL Server, omit `--network host` and use the accessible hostname in your connection string.

### Option 2: .NET Global Tool (NuGet)

Requires .NET 9 SDK or later.

```bash
dotnet tool install -g Neofenyx.SqlSentinel.Mcp
```

```json
{
  "mcpServers": {
    "sql-sentinel": {
      "command": "sql-sentinel-mcp"
    }
  }
}
```

### Option 3: Build from Source

```bash
git clone https://github.com/tkmawarire/sql-sentinel.git
cd sql-sentinel
dotnet build
```

Run directly:

```bash
dotnet run --project SqlServer.Profiler.Mcp/
```

Or publish a self-contained single binary:

```bash
# Windows
dotnet publish SqlServer.Profiler.Mcp/ -c Release -r win-x64 --self-contained

# Linux
dotnet publish SqlServer.Profiler.Mcp/ -c Release -r linux-x64 --self-contained

# macOS (Apple Silicon)
dotnet publish SqlServer.Profiler.Mcp/ -c Release -r osx-arm64 --self-contained

# macOS (Intel)
dotnet publish SqlServer.Profiler.Mcp/ -c Release -r osx-x64 --self-contained
```

Output will be in `bin/Release/net9.0/{runtime}/publish/`

## Connection Strings

**SQL Authentication:**
```
Server=localhost;Database=master;User Id=sa;Password=YourPassword;TrustServerCertificate=true
```

**Windows Authentication:**
```
Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true
```

**Azure SQL:**
```
Server=yourserver.database.windows.net;Database=yourdb;User Id=user;Password=password;Encrypt=true
```

## MCP Tools Reference

### Session Lifecycle

| Tool | Description |
|------|-------------|
| `sqlsentinel_create_session` | Create an Extended Events session with filters (not started) |
| `sqlsentinel_start_session` | Start capturing events for an existing session |
| `sqlsentinel_stop_session` | Stop capturing; events are retained |
| `sqlsentinel_drop_session` | Drop session and discard all events |
| `sqlsentinel_list_sessions` | List all MCP-created sessions with state and buffer usage |
| `sqlsentinel_quick_capture` | Create and start a session in one step |

### Event Retrieval

| Tool | Description |
|------|-------------|
| `sqlsentinel_get_events` | Retrieve captured events with filtering, sorting, and deduplication |
| `sqlsentinel_get_stats` | Aggregate statistics grouped by fingerprint, database, app, or login |
| `sqlsentinel_analyze_sequence` | Analyze query execution sequence with timing and gaps |
| `sqlsentinel_get_connection_info` | List databases, applications, logins, sessions, and blocking info |
| `sqlsentinel_stream_events` | Real-time event capture for a specified duration (1–300s) |

### Diagnostics

| Tool | Description |
|------|-------------|
| `sqlsentinel_get_deadlocks` | Retrieve deadlock events with victim, processes, locks, and SQL text |
| `sqlsentinel_get_blocking` | Retrieve blocked process events with wait resources and SQL text |
| `sqlsentinel_get_wait_stats` | Query `sys.dm_os_wait_stats` categorized by type (no session required) |
| `sqlsentinel_health_check` | Comprehensive report: slow queries, deadlocks, blocking, wait stats, insights |

### Permissions

| Tool | Description |
|------|-------------|
| `sqlsentinel_check_permissions` | Check current login permissions and blocked process threshold config |
| `sqlsentinel_grant_permissions` | Grant required permissions to a login (requires sysadmin) |

## Usage Examples

### Quick Debug Session

```
Agent: sqlsentinel_quick_capture(
    sessionName: "debug_api",
    connectionString: "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true",
    applications: "MyWebApp",
    minDurationMs: 100
)

// User triggers the slow operation

Agent: sqlsentinel_get_events(
    sessionName: "debug_api",
    sortBy: "DurationDesc",
    limit: 20
)

Agent: sqlsentinel_drop_session(sessionName: "debug_api", ...)
```

### Find N+1 Queries

```
Agent: sqlsentinel_quick_capture(
    sessionName: "n_plus_one_check",
    databases: "OrdersDB"
)

// User loads a page

Agent: sqlsentinel_get_stats(
    sessionName: "n_plus_one_check",
    groupBy: "QueryFingerprint"
)

// Look for queries with high execution counts
```

### Trace Specific Operation

```
Agent: sqlsentinel_analyze_sequence(
    sessionName: "my_session",
    correlationId: "order-12345",
    responseFormat: "Markdown"
)
```

### Deadlock Detection

```
Agent: sqlsentinel_quick_capture(
    sessionName: "deadlock_monitor",
    connectionString: "...",
    eventTypes: "Deadlock"
)

// Wait for deadlocks to occur

Agent: sqlsentinel_get_deadlocks(
    sessionName: "deadlock_monitor",
    responseFormat: "Markdown"
)
```

### Blocking Analysis

```
Agent: sqlsentinel_quick_capture(
    sessionName: "blocking_check",
    connectionString: "...",
    eventTypes: "BlockedProcess"
)

// Requires: sp_configure 'blocked process threshold', 5

Agent: sqlsentinel_get_blocking(
    sessionName: "blocking_check",
    responseFormat: "Markdown"
)
```

### Server Health Check

```
Agent: sqlsentinel_health_check(
    connectionString: "...",
    sessionName: "my_session",
    slowQueryThresholdMs: 1000,
    responseFormat: "Markdown"
)
```

### Wait Stats (No Session Required)

```
Agent: sqlsentinel_get_wait_stats(
    connectionString: "...",
    topN: 20,
    responseFormat: "Markdown"
)
```

## Query Fingerprinting

Queries are normalized to group similar ones:

```sql
-- These become one fingerprint:
SELECT * FROM Users WHERE id = 123
SELECT * FROM Users WHERE id = 456

-- Fingerprint: abc123:SELECT * FROM Users WHERE id = ?
-- Execution count: 2
```

## Noise Filtering

Default excluded patterns (when `excludeNoise=true`):

- `sp_reset_connection` — Connection pool reset
- `SET TRANSACTION ISOLATION LEVEL` — Session setup
- `SET NOCOUNT`, `SET ANSI_*` — Client configuration
- `sp_trace_*`, `fn_trace_*` — Trace system queries

## Supported Event Types

`SqlBatchCompleted`, `RpcCompleted`, `SqlStatementCompleted`, `SpStatementCompleted`, `Attention`, `ErrorReported`, `Deadlock`, `BlockedProcess`, `LoginEvent`, `SchemaChange`, `Recompile`, `AutoStats`

## Project Structure

```
sql-profiler-mcp/
├── .github/
│   └── workflows/
│       ├── docker.yml                     # Build & push multi-arch Docker images
│       └── publish-mcp-registry.yml       # Publish NuGet + MCP registry
├── .mcp/
│   └── server.json                        # MCP manifest (NuGet + OCI packages)
├── SqlServer.Profiler.Mcp/                # Main MCP server (stdio transport)
│   ├── SqlServer.Profiler.Mcp.csproj
│   ├── Program.cs                         # Entry point, DI setup, MCP config
│   ├── Models/
│   │   └── ProfilerModels.cs              # Records, enums, data models
│   ├── Services/
│   │   ├── ProfilerService.cs             # Core Extended Events logic
│   │   ├── QueryFingerprintService.cs     # SQL normalization & fingerprinting
│   │   ├── WaitStatsService.cs            # DMV-based wait stats analysis
│   │   ├── SessionConfigStore.cs          # In-memory session config storage
│   │   └── EventStreamingService.cs       # Real-time event streaming
│   └── Tools/
│       ├── SessionManagementTools.cs      # Session lifecycle tools (6)
│       ├── EventRetrievalTools.cs         # Event retrieval tools (5)
│       ├── DiagnosticTools.cs             # Diagnostic tools (4)
│       └── PermissionTools.cs             # Permission tools (2)
├── SqlServer.Profiler.Mcp.Api/            # Debug REST API (Swagger on port 5100)
│   ├── SqlServer.Profiler.Mcp.Api.csproj
│   ├── Program.cs
│   ├── Controllers/
│   │   └── ProfilerController.cs
│   ├── Models/
│   │   └── RequestModels.cs
│   └── appsettings.json
├── SqlServer.Profiler.Mcp.Cli/            # Debug CLI (REPL + script mode)
│   ├── SqlServer.Profiler.Mcp.Cli.csproj
│   └── Program.cs
├── Dockerfile                             # Multi-stage build (bookworm-slim)
├── .dockerignore
├── SqlServer.Profiler.Mcp.slnx           # Solution file
├── CLAUDE.md
├── CONTRIBUTING.md
└── README.md
```

## Development

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- SQL Server 2012+ instance (local, Docker, or remote)
- Docker (optional, for container builds)

### Clone & Build

```bash
git clone https://github.com/tkmawarire/sql-sentinel.git
cd sql-sentinel
dotnet restore
dotnet build
```

### Running the MCP Server Locally

```bash
dotnet run --project SqlServer.Profiler.Mcp/
```

The server communicates over stdio using the MCP protocol. Connect it to an MCP client (Claude Desktop, Claude Code, etc.) for interactive use.

### Using the Debug API

The API project provides a REST wrapper around all MCP tools with Swagger UI for manual testing.

```bash
dotnet run --project SqlServer.Profiler.Mcp.Api/
```

- Swagger UI: `http://localhost:5100/`
- All endpoints accept a `connectionString` query parameter, or you can configure it via:
  - `appsettings.json` → `SqlSentinel:ConnectionString`
  - Environment variable → `SQL_SENTINEL_CONNECTION_STRING`

### Using the Debug CLI

The CLI project provides an interactive REPL and script mode for testing tools directly.

```bash
# Interactive REPL mode
dotnet run --project SqlServer.Profiler.Mcp.Cli/

# List all available tools
dotnet run --project SqlServer.Profiler.Mcp.Cli/ list

# Get help for a specific tool
dotnet run --project SqlServer.Profiler.Mcp.Cli/ help sqlsentinel_quick_capture

# Execute a single tool
dotnet run --project SqlServer.Profiler.Mcp.Cli/ call sqlsentinel_list_sessions --connection-string "Server=localhost;..."
```

Set the `SQL_SENTINEL_CONNECTION_STRING` environment variable to avoid passing it on every call.

### Docker Build

```bash
docker build -t sql-sentinel-mcp:test .
docker run -i --rm --network host sql-sentinel-mcp:test
```

## Architecture

### Key Patterns

- **Dependency injection** via `Microsoft.Extensions.Hosting`
- **stdio transport** — stdout is reserved for MCP protocol; all logging goes to stderr
- **Tool auto-discovery** — MCP tools are discovered from the assembly via `WithToolsFromAssembly()`
- **XE session prefix** — All created sessions are prefixed with `mcp_sentinel_`
- **Two event shapes** — Standard events (query, login, recompile) with typed fields, and XML-payload events (deadlock, blocking) parsed from Extended Events XML

### Adding a New MCP Tool

1. Create a `public static` method in the appropriate file under `Tools/` (or create a new file)
2. Decorate with `[McpServerTool(Name = "sqlsentinel_your_tool")]` and `[Description("...")]`
3. Add parameters with `[Description("...")]` attributes — they become the tool's input schema
4. Inject services via method parameters (e.g., `IProfilerService`, `IWaitStatsService`)
5. Return a string (JSON or Markdown) — the framework handles MCP response wrapping

```csharp
[McpServerTool(Name = "sqlsentinel_example")]
[Description("Description shown to AI agents")]
public static async Task<string> Example(
    IProfilerService profilerService,
    [Description("SQL Server connection string")] string connectionString,
    [Description("Optional filter")] string? filter = null)
{
    // Implementation
    return JsonSerializer.Serialize(result);
}
```

## Troubleshooting

### "Permission denied" creating session
```sql
GRANT ALTER ANY EVENT SESSION TO [your_login];
GRANT VIEW SERVER STATE TO [your_login];
```

### "Login failed"
- Check connection string credentials
- For Windows auth, ensure process runs under correct user
- For Azure SQL, ensure firewall allows your IP

### No events captured
1. Verify session is RUNNING (`sqlsentinel_list_sessions`)
2. Check filters aren't too restrictive
3. Verify target database/app is generating queries
4. Check `minDurationMs` isn't filtering everything

### No deadlock events
- Ensure session was created with `eventTypes: "Deadlock"`
- Deadlocks must actually occur while the session is running

### No blocking events
- Ensure `blocked process threshold` is configured: `sp_configure 'blocked process threshold', 5`
- Ensure session was created with `eventTypes: "BlockedProcess"`
- Blocking must exceed the configured threshold (seconds)

### Timeout reading events
Large ring buffers with many events can be slow to parse. Use:
- Time filters to narrow the window
- Increase command timeout in code if needed

## Security Notes

- Connection strings contain credentials — secure appropriately
- Don't leave sessions running indefinitely on production
- Query text may contain sensitive data
- Grant minimum required permissions

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines on submitting issues and pull requests.

## License

MIT
