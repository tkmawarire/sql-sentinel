# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SQL Sentinel (v2.4.0) - a .NET 9 MCP (Model Context Protocol) server for SQL Server monitoring, diagnostics, and database operations using Extended Events and DMVs. Includes a real-time Blazor Server monitoring dashboard, a REST API, and a CLI. Provides query profiling, deadlock detection, blocking analysis, wait stats, intelligent diagnostics, analytics (SQL parsing, trend analysis, anomaly detection), and CRUD database tools. Uses Microsoft.Data.SqlClient for native SQL Server connectivity (no ODBC drivers required).

## Build Commands

```bash
# Restore and build entire solution
dotnet restore
dotnet build

# Run the MCP server
dotnet run --project SqlServer.Profiler.Mcp

# Run the Dashboard (port 5200)
dotnet run --project SqlServer.Profiler.Mcp.Dashboard

# Run tests
dotnet test SqlServer.Profiler.Mcp.Tests

# Publish single executable (pick your platform)
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained
dotnet publish -c Release -r osx-arm64 --self-contained
```

Output location: `bin/Release/net9.0/{runtime}/publish/`

## Architecture

```
SqlServer.Profiler.Mcp/                # Core MCP server library
├── Program.cs                          # Entry point, DI setup, MCP server config
├── Services/
│   ├── ProfilerService.cs              # Core Extended Events logic (IProfilerService)
│   ├── QueryFingerprintService.cs      # SQL normalization & fingerprinting (IQueryFingerprintService)
│   ├── WaitStatsService.cs             # DMV-based wait stats analysis (IWaitStatsService)
│   ├── EventStreamingService.cs        # Real-time event streaming via Channels (IEventStreamingService)
│   ├── MemoryService.cs                # Historical query memory & baselines (IMemoryService)
│   ├── SqlParsingService.cs            # SQL text parsing: tables, SPs, operations (ISqlParsingService)
│   ├── TrendAnalysisService.cs         # Period comparison, anomaly detection, trends (ITrendAnalysisService)
│   └── SessionConfigStore.cs           # In-memory session config storage
├── Models/
│   ├── ProfilerModels.cs               # Records, enums (SessionConfig, ProfilerEvent, DeadlockEvent, etc.)
│   ├── AnalyticsModels.cs              # Analytics records (TableStats, ProcedureStats, AnomalyResult, etc.)
│   └── DbOperationResult.cs            # Result model for CRUD database operations
├── Utilities/
│   └── SqlInputValidator.cs            # SQL input validation & escaping (shared across tools)
└── Tools/
    ├── SessionManagementTools.cs       # MCP tools: create/start/stop/drop/list/quick_capture
    ├── EventRetrievalTools.cs          # MCP tools: get_events/get_stats/analyze_sequence/get_connection_info
    ├── DiagnosticTools.cs              # MCP tools: get_deadlocks/get_blocking/get_wait_stats/health_check
    ├── AnalyticsTools.cs               # MCP tools: stats_by_table/procedure/operation, compare, anomalies, trends
    ├── PermissionTools.cs              # MCP tools: check_permissions/grant_permissions
    └── DatabaseTools.cs               # MCP tools: list_tables/describe_table/create_table/read_data/insert_data/update_data/drop_table

SqlServer.Profiler.Mcp.Dashboard/      # Blazor Server real-time monitoring dashboard (port 5200)
├── Program.cs                          # Kestrel setup, DI, SignalR hubs, MudBlazor
├── Hubs/
│   ├── EventStreamHub.cs              # SignalR hub for live event streaming to browser
│   └── DiagnosticsHub.cs             # SignalR hub for diagnostics broadcasts
├── Services/
│   ├── DashboardEventBroadcaster.cs   # Bridges EventStreamingService → SignalR groups
│   └── DashboardMetricsService.cs     # Polls diagnostics every 5s → SignalR broadcast
├── Components/
│   ├── App.razor, Routes.razor        # Root components
│   ├── Layout/ (MainLayout, NavMenu)  # Dark theme shell with mini drawer
│   ├── Pages/
│   │   ├── LiveStream.razor           # Real-time event stream with filters & auto-scroll
│   │   ├── QueryAnalytics.razor       # Group-by analytics (Fingerprint/SP/Table/DB/App/Operation)
│   │   ├── Diagnostics.razor          # Health, deadlocks, blocking, wait stats, anomalies
│   │   └── Sessions.razor             # Session management + Quick Capture wizard
│   └── Shared/ (EventCard, MetricTile, ConnectionStatus, DrillDownDialog, CreateSessionDialog)
└── wwwroot/css/app.css                # Dark theme custom CSS

SqlServer.Profiler.Mcp.Api/            # REST API project
SqlServer.Profiler.Mcp.Tests/          # xUnit test suite (300+ tests)
```

**Key patterns:**
- Dependency injection via Microsoft.Extensions.Hosting
- MCP tools auto-discovered from assembly via `WithToolsFromAssembly()`
- Logging to stderr (stdout reserved for MCP protocol)
- All tools return JSON with optional Markdown formatting
- XE session prefix: `mcp_sentinel_`
- Two event definition shapes: standard events (query/login/recompile) and XML-payload events (deadlock/blocking)
- Dashboard uses SignalR to bridge `EventStreamingService` channels to the browser
- Connection string via `SQL_SENTINEL_CONNECTION_STRING` env var (or `.env` file)
- Dashboard tech: MudBlazor 8.7.0 + Blazor-ApexCharts 4.0.0, dark theme (slate-900/cyan-500)

## SQL Server Requirements

- SQL Server 2012+ with Extended Events
- Required permissions:
  ```sql
  GRANT ALTER ANY EVENT SESSION TO [your_login];
  GRANT VIEW SERVER STATE TO [your_login];
  ```
- For blocked process detection: `EXEC sp_configure 'blocked process threshold', 5; RECONFIGURE;`

## MCP Tools Summary

**Session lifecycle:** `sqlsentinel_create_session`, `sqlsentinel_start_session`, `sqlsentinel_stop_session`, `sqlsentinel_drop_session`, `sqlsentinel_list_sessions`, `sqlsentinel_quick_capture`

**Event retrieval:** `sqlsentinel_get_events`, `sqlsentinel_get_stats`, `sqlsentinel_analyze_sequence`, `sqlsentinel_get_connection_info`

**Diagnostics:** `sqlsentinel_get_deadlocks`, `sqlsentinel_get_blocking`, `sqlsentinel_get_wait_stats`, `sqlsentinel_health_check`

**Analytics:** `sqlsentinel_get_stats_by_table`, `sqlsentinel_get_stats_by_procedure`, `sqlsentinel_get_stats_by_operation`, `sqlsentinel_compare_periods`, `sqlsentinel_detect_anomalies`, `sqlsentinel_get_query_trend`

**Permissions:** `sqlsentinel_check_permissions`, `sqlsentinel_grant_permissions`

**Database operations:** `sqlsentinel_list_tables`, `sqlsentinel_describe_table`, `sqlsentinel_create_table`, `sqlsentinel_insert_data`, `sqlsentinel_read_data`, `sqlsentinel_update_data`, `sqlsentinel_drop_table`

## Supported Event Types

`SqlBatchCompleted`, `RpcCompleted`, `SqlBatchStarting`, `RpcStarting`, `ErrorReported`, `AttentionEvent`, `Deadlock`, `BlockedProcess`, `LoginEvent`, `SchemaChange`, `Recompile`, `AutoStats`
