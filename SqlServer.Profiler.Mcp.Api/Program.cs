using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using SqlServer.Profiler.Mcp.Services;
using SqlServer.Profiler.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to listen on port 5100
builder.WebHost.UseUrls("http://localhost:5100");

// Register core profiler services as singletons
builder.Services.AddSingleton<IProfilerService, ProfilerService>();
builder.Services.AddSingleton<IQueryFingerprintService, QueryFingerprintService>();
builder.Services.AddSingleton<IWaitStatsService, WaitStatsService>();
builder.Services.AddSingleton<SessionConfigStore>();
builder.Services.AddSingleton<EventStreamingService>();
builder.Services.AddSingleton<IEventStreamingService>(sp => sp.GetRequiredService<EventStreamingService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventStreamingService>());

// Register MCP tool classes as transient (they are lightweight wrappers)
builder.Services.AddTransient<SessionManagementTools>();
builder.Services.AddTransient<EventRetrievalTools>();
builder.Services.AddTransient<PermissionTools>();
builder.Services.AddTransient<DiagnosticTools>();

// Add controllers with XML documentation for Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "SQL Sentinel - Debug API",
        Version = "v1",
        Description = "REST API for debugging SQL Sentinel MCP server tools without connecting to an AI agent."
    });

    // Include XML comments for Swagger parameter descriptions
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

var app = builder.Build();

// Global exception handler — returns sanitized errors, never exposes internals
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = exceptionFeature?.Error;

        if (exception != null)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        var error = new
        {
            error = "An internal error occurred. Check server logs for details.",
            traceId = context.TraceIdentifier
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(error));
    });
});

// Enable Swagger UI in all environments for debugging purposes
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "SQL Sentinel - Debug API v1");
    options.RoutePrefix = string.Empty; // Serve Swagger UI at root
});

app.MapControllers();

app.Run();
