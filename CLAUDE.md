# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SQL Server Profiler MCP Server - a .NET 9 MCP (Model Context Protocol) server for SQL Server query profiling using Extended Events. Uses Microsoft.Data.SqlClient for native SQL Server connectivity (no ODBC drivers required).

## Build Commands

```bash
# Restore and build
dotnet restore
dotnet build

# Run the server
dotnet run

# Publish single executable (pick your platform)
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained
dotnet publish -c Release -r osx-arm64 --self-contained
```

Output location: `bin/Release/net9.0/{runtime}/publish/`

## Architecture

```
Program.cs                          # Entry point, DI setup, MCP server config
├── Services/
│   ├── ProfilerService.cs          # Core Extended Events logic (IProfilerService)
│   ├── QueryFingerprintService.cs  # SQL normalization & fingerprinting (IQueryFingerprintService)
│   └── SessionConfigStore.cs       # In-memory session config storage
├── Models/
│   └── ProfilerModels.cs           # Records, enums (SessionConfig, ProfilerEvent, EventFilters, etc.)
└── Tools/
    ├── SessionManagementTools.cs   # MCP tools: create/start/stop/drop/list/quick_capture
    ├── EventRetrievalTools.cs      # MCP tools: get_events/get_stats/analyze_sequence/get_connection_info
    └── PermissionTools.cs          # MCP tools: check_permissions/grant_permissions
```

**Key patterns:**
- Dependency injection via Microsoft.Extensions.Hosting
- MCP tools auto-discovered from assembly via `WithToolsFromAssembly()`
- Logging to stderr (stdout reserved for MCP protocol)
- All tools return JSON with optional Markdown formatting

## SQL Server Requirements

- SQL Server 2012+ with Extended Events
- Required permissions:
  ```sql
  GRANT ALTER ANY EVENT SESSION TO [your_login];
  GRANT VIEW SERVER STATE TO [your_login];
  ```

## MCP Tools Summary

**Session lifecycle:** `sqlprofiler_create_session`, `sqlprofiler_start_session`, `sqlprofiler_stop_session`, `sqlprofiler_drop_session`, `sqlprofiler_list_sessions`, `sqlprofiler_quick_capture`

**Event retrieval:** `sqlprofiler_get_events`, `sqlprofiler_get_stats`, `sqlprofiler_analyze_sequence`, `sqlprofiler_get_connection_info`

**Permissions:** `sqlprofiler_check_permissions`, `sqlprofiler_grant_permissions`
