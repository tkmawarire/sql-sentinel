<!-- mcp-name: io.github.tkmawarire/sql-sentinel -->
# SQL Server Profiler MCP Server (.NET)

A production-ready MCP (Model Context Protocol) server for SQL Server query profiling using Extended Events. Built with .NET 9 (compatible with .NET 10) and Microsoft.Data.SqlClient for **native SQL Server connectivity—NO ODBC DRIVERS REQUIRED**.

## Why .NET?

| ODBC/pyodbc | Microsoft.Data.SqlClient |
|-------------|--------------------------|
| Requires ODBC driver installation | Native .NET - just works |
| Platform-specific driver setup | Cross-platform out of the box |
| Additional dependency | Built into .NET ecosystem |
| Connection string complexity | Standard SQL Server format |

## Why Extended Events?

| SQL Trace (Profiler GUI) | Extended Events (This Server) |
|--------------------------|-------------------------------|
| Deprecated since SQL 2012 | Actively developed, future-proof |
| 5-10% performance overhead | 1-2% overhead |
| Will be removed | Microsoft's strategic direction |

## Features

- **Session Management**: Create/Start/Stop/Drop lifecycle
- **Smart Filtering**: By app, database, user, duration, host, text patterns
- **Production-Safe**: Auto-excludes noise (`sp_reset_connection`, `SET` statements, etc.)
- **Query Fingerprinting**: Groups similar queries differing only in literals
- **Sequence Analysis**: Trace execution order with gaps and cumulative timing
- **AI-Optimized**: Structured JSON output with markdown option

## Requirements

- .NET 9+ SDK (or .NET 8 with minor changes)
- SQL Server 2012+ with Extended Events enabled (default)
- SQL Server permissions:
  ```sql
  GRANT ALTER ANY EVENT SESSION TO [your_login];
  GRANT VIEW SERVER STATE TO [your_login];
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

## Tools Reference

### Session Lifecycle

| Tool | Description |
|------|-------------|
| `sqlprofiler_create_session` | Create profiling session with filters |
| `sqlprofiler_start_session` | Begin capturing events |
| `sqlprofiler_stop_session` | Pause capture (retain events) |
| `sqlprofiler_drop_session` | Delete session and all data |
| `sqlprofiler_list_sessions` | Show all MCP profiler sessions |
| `sqlprofiler_quick_capture` | Create + start in one step |

### Event Retrieval

| Tool | Description |
|------|-------------|
| `sqlprofiler_get_events` | Retrieve queries with filtering/pagination |
| `sqlprofiler_get_stats` | Aggregate stats by query/db/app/login |
| `sqlprofiler_analyze_sequence` | Trace execution order for an operation |
| `sqlprofiler_get_connection_info` | List databases, apps, logins, sessions |

### Permissions

| Tool | Description |
|------|-------------|
| `sqlprofiler_check_permissions` | Check if login has required permissions, provide GRANT statements if missing |
| `sqlprofiler_grant_permissions` | Grant profiling permissions to a login (requires sysadmin) |

## Usage Examples

### Quick Debug Session

```
Agent: sqlprofiler_quick_capture(
    sessionName: "debug_api",
    connectionString: "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true",
    applications: "MyWebApp",
    minDurationMs: 100
)

// User triggers the slow operation

Agent: sqlprofiler_get_events(
    sessionName: "debug_api",
    sortBy: "DurationDesc",
    limit: 20
)

Agent: sqlprofiler_drop_session(sessionName: "debug_api", ...)
```

### Find N+1 Queries

```
Agent: sqlprofiler_quick_capture(
    sessionName: "n_plus_one_check",
    databases: "OrdersDB"
)

// User loads a page

Agent: sqlprofiler_get_stats(
    sessionName: "n_plus_one_check",
    groupBy: "QueryFingerprint"
)

// Look for queries with high execution counts
```

### Trace Specific Operation

```
Agent: sqlprofiler_analyze_sequence(
    sessionName: "my_session",
    correlationId: "order-12345",
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

- `sp_reset_connection` - Connection pool reset
- `SET TRANSACTION ISOLATION LEVEL` - Session setup
- `SET NOCOUNT`, `SET ANSI_*` - Client configuration
- `sp_trace_*`, `fn_trace_*` - Trace system queries

## Project Structure

```
sqlserver-profiler-mcp-dotnet/
├── SqlServerProfilerMcp.csproj
├── Program.cs                    # Entry point, MCP server setup
├── Models/
│   └── ProfilerModels.cs         # Data models
├── Services/
│   ├── ProfilerService.cs        # Core XEvents logic
│   ├── QueryFingerprintService.cs
│   └── SessionConfigStore.cs
└── Tools/
    ├── SessionManagementTools.cs # Session lifecycle tools
    ├── EventRetrievalTools.cs    # Event query/analysis tools
    └── PermissionTools.cs        # Permission check/grant tools
```

## Upgrading to .NET 10

When .NET 10 releases:

1. Update `SqlServerProfilerMcp.csproj`:
   ```xml
   <TargetFramework>net10.0</TargetFramework>
   ```

2. Update NuGet packages:
   ```bash
   dotnet add package Microsoft.Data.SqlClient --version <latest>
   dotnet add package ModelContextProtocol --version <latest>
   ```

3. Rebuild:
   ```bash
   dotnet build
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
1. Verify session is RUNNING (`sqlprofiler_list_sessions`)
2. Check filters aren't too restrictive
3. Verify target database/app is generating queries
4. Check `minDurationMs` isn't filtering everything

### Timeout reading events
Large ring buffers with many events can be slow to parse. Use:
- Time filters to narrow the window
- Increase command timeout in code if needed

## Security Notes

- Connection strings contain credentials - secure appropriately
- Don't leave sessions running indefinitely on production
- Query text may contain sensitive data
- Grant minimum required permissions

## License

MIT
