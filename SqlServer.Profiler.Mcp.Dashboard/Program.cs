using MudBlazor;
using MudBlazor.Services;
using SqlServer.Profiler.Mcp.Services;
using SqlServer.Profiler.Mcp.Dashboard.Hubs;
using SqlServer.Profiler.Mcp.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel
builder.WebHost.UseUrls("http://localhost:5200");

// Configure logging
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Register core SQL Sentinel services (same as MCP server and API)
builder.Services.AddSingleton<IProfilerService, ProfilerService>();
builder.Services.AddSingleton<IQueryFingerprintService, QueryFingerprintService>();
builder.Services.AddSingleton<IWaitStatsService, WaitStatsService>();
builder.Services.AddSingleton<SessionConfigStore>();
builder.Services.AddSingleton<EventStreamingService>();
builder.Services.AddSingleton<IEventStreamingService>(sp => sp.GetRequiredService<EventStreamingService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventStreamingService>());
builder.Services.AddSingleton<IMemoryService, MemoryService>();
builder.Services.AddHostedService(sp => (MemoryService)sp.GetRequiredService<IMemoryService>());
builder.Services.AddSingleton<ISqlParsingService, SqlParsingService>();
builder.Services.AddSingleton<ITrendAnalysisService, TrendAnalysisService>();

// Dashboard-specific services
builder.Services.AddSingleton<DashboardEventBroadcaster>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardEventBroadcaster>());
builder.Services.AddSingleton<DashboardMetricsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DashboardMetricsService>());

// Blazor + MudBlazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = true;
    config.SnackbarConfiguration.ShowTransitionDuration = 300;
    config.SnackbarConfiguration.HideTransitionDuration = 300;
});

builder.Services.AddSignalR();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<SqlServer.Profiler.Mcp.Dashboard.Components.App>()
    .AddInteractiveServerRenderMode();

// SignalR hubs
app.MapHub<EventStreamHub>("/hubs/events");
app.MapHub<DiagnosticsHub>("/hubs/diagnostics");

app.Run();
