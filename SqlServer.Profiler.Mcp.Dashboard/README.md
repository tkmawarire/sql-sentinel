# SQL Sentinel Dashboard

Real-time SQL Server monitoring dashboard built with Blazor Server. Provides live event streaming, query analytics, diagnostics, and session management through a dark-themed web UI.

## Quick Start

```bash
# Set connection string
export SQL_SENTINEL_CONNECTION_STRING="Server=your-server;Database=master;User Id=sa;Password=...;TrustServerCertificate=true"

# Run the dashboard
dotnet run --project SqlServer.Profiler.Mcp.Dashboard
```

Open [http://localhost:5200](http://localhost:5200) in your browser.

## Prerequisites

- .NET 9 SDK
- SQL Server 2012+ with Extended Events enabled
- Required SQL Server permissions:
  ```sql
  GRANT ALTER ANY EVENT SESSION TO [your_login];
  GRANT VIEW SERVER STATE TO [your_login];
  ```

## Pages

### Live Event Stream (`/`)

Real-time event monitoring with auto-scrolling data grid.

- **Session selector** — pick from existing Extended Events sessions, start/pause/stop streaming
- **Metric tiles** — events/sec, total events, average duration, active session status
- **Filter bar** — search SQL text, filter by database, filter by duration threshold
- **Event table** — fixed header with scrollable body, color-coded by severity (slow queries amber/red, errors red)
- **Row click** — opens event detail dialog with full SQL text, execution stats, and query fingerprint

Events stream via SignalR from the `EventStreamHub`. The dashboard polls the XE ring buffer and broadcasts events to subscribed browser clients.

### Query Analytics (`/analytics`)

Group-by analytics with charts and drill-down.

- **Group by** — Fingerprint, Stored Procedure, Table, Database, Application, or Operation type
- **Data grid** — sortable by count, avg duration, max duration, reads, writes
- **Charts** — ApexCharts time-series for execution count and average duration over time
- **Drill-down** — click a row to see individual queries and trend sparklines

### Diagnostics (`/diagnostics`)

Server health monitoring across multiple tabs.

- **Health Overview** — severity cards and actionable insights from `sqlsentinel_health_check`
- **Deadlocks** — deadlock event timeline with process/resource details
- **Blocking** — live blocking chain tree with auto-refresh via `DiagnosticsHub`
- **Wait Stats** — donut chart by category, bar chart top 20, full data grid
- **Anomalies** — anomaly detection comparing current session data against historical baselines

### Session Manager (`/sessions`)

Create, start, stop, and drop Extended Events profiling sessions.

- **Session list** — name, state (Running/Stopped chip), buffer usage progress bar, created date
- **Actions** — start, stop, drop buttons per session
- **Quick Capture** — one-click wizard: select duration, auto-creates a temporary session, captures events, then cleans up

## Architecture

```
SqlServer.Profiler.Mcp.Dashboard/
├── Program.cs                          # Kestrel (port 5200), DI, SignalR hubs, MudBlazor
├── Hubs/
│   ├── EventStreamHub.cs              # Live event streaming: Subscribe/Unsubscribe per session
│   └── DiagnosticsHub.cs             # Periodic diagnostics broadcast (blocking, wait stats)
├── Services/
│   ├── DashboardEventBroadcaster.cs   # IHostedService: bridges EventStreamingService → SignalR
│   └── DashboardMetricsService.cs     # IHostedService: polls diagnostics every 5s → SignalR
├── Components/
│   ├── App.razor                      # Root component with MudBlazor providers
│   ├── Routes.razor                   # Router configuration
│   ├── Layout/
│   │   ├── MainLayout.razor           # Dark theme shell: app bar, mini drawer, main content
│   │   └── NavMenu.razor              # Navigation links with icons
│   ├── Pages/
│   │   ├── LiveStream.razor           # Real-time event stream with filters & auto-scroll
│   │   ├── QueryAnalytics.razor       # Group-by analytics with charts and drill-down
│   │   ├── Diagnostics.razor          # Health, deadlocks, blocking, wait stats, anomalies
│   │   └── Sessions.razor             # Session management + Quick Capture wizard
│   └── Shared/
│       ├── ConnectionStatus.razor     # SignalR connection indicator in app bar
│       ├── MetricTile.razor           # Reusable metric card with label, value, subtitle
│       ├── EventCard.razor            # Event detail dialog (full SQL, stats, fingerprint)
│       ├── DrillDownDialog.razor      # Query group drill-down with individual events
│       └── CreateSessionDialog.razor  # New session form with event type selection
└── wwwroot/
    └── css/app.css                    # Dark theme overrides, scrollbar styling, layout rules
```

## Tech Stack

| Package | Version | Purpose |
|---------|---------|---------|
| [MudBlazor](https://mudblazor.com/) | 8.7.0 | UI components: DataGrid, Dialog, Tabs, Charts, Theme |
| [Blazor-ApexCharts](https://github.com/apexcharts/Blazor-ApexCharts) | 4.0.0 | Time-series, heatmaps, sparklines, donut charts |
| Microsoft.AspNetCore.SignalR.Client | 9.0.x | Real-time event streaming to browser |

## Design

- **Dark theme** — slate-900 background (`#020617`), cyan-500 accent (`#06b6d4`), Inter font
- **Color coding** — green (success/running), amber (slow >1s), red (errors/critical >5s), cyan (info)
- **Mini drawer** — collapsible sidebar with icon-only mode
- **Viewport-fit layout** — page never scrolls; only the event table body scrolls with a fixed header

## Real-Time Data Flow

```
SQL Server XE Ring Buffer
    ↓ (poll every 500ms–5s, adaptive)
EventStreamingService (Channel<ProfilerEvent>)
    ↓
DashboardEventBroadcaster (IHostedService)
    ↓ (SignalR group per session)
EventStreamHub → Browser (Blazor Server WebSocket)
    ↓
LiveStream.razor (circular buffer, 5000 events max)
```

The `DashboardEventBroadcaster` bridges the core `EventStreamingService` channels to SignalR groups. Each session gets its own group (`session:{name}`). The polling interval adapts: 500ms when events are flowing, backs off to 5s during idle periods.

`DashboardMetricsService` separately polls diagnostics (active blocking, wait stats, session list) every 5 seconds and broadcasts to `DiagnosticsHub` subscribers.

## Configuration

The dashboard reads the SQL Server connection string from:

1. `SQL_SENTINEL_CONNECTION_STRING` environment variable
2. `.env` file in the project root (format: `ConnectionString="Server=...;..."`)

### Port

Default: `http://localhost:5200`. Change in `Program.cs`:

```csharp
builder.WebHost.UseUrls("http://localhost:5200");
```

## Shared Services

The dashboard reuses the same core services as the MCP server and REST API:

| Service | Purpose |
|---------|---------|
| `IProfilerService` | XE session lifecycle, event retrieval, stats |
| `IQueryFingerprintService` | SQL normalization and fingerprinting |
| `IWaitStatsService` | DMV-based wait stats analysis |
| `IEventStreamingService` | Real-time event streaming via Channels |
| `IMemoryService` | Historical query memory and baselines |
| `ISqlParsingService` | SQL text parsing: tables, SPs, operations |
| `ITrendAnalysisService` | Period comparison, anomaly detection, trends |

## Development

```bash
# Build
dotnet build SqlServer.Profiler.Mcp.Dashboard

# Run with hot reload
dotnet watch --project SqlServer.Profiler.Mcp.Dashboard

# Run tests (core services)
dotnet test SqlServer.Profiler.Mcp.Tests
```

CSS changes in `wwwroot/css/app.css` are served as static files and take effect on hard reload (Ctrl+Shift+R). Razor component changes require the Blazor Server hot-reload or a restart.
