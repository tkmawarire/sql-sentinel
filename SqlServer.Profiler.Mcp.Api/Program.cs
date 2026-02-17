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

// Enable Swagger UI in all environments for debugging purposes
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "SQL Sentinel - Debug API v1");
    options.RoutePrefix = string.Empty; // Serve Swagger UI at root
});

app.MapControllers();

app.Run();
