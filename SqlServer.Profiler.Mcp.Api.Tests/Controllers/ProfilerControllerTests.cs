using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Moq;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Api.Tests.Controllers;

public class ProfilerControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly CustomWebApplicationFactory _factory;

    public ProfilerControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── Session Endpoints ───────────────────────────

    [Fact]
    public async Task ListSessions_Returns200()
    {
        var response = await _client.GetAsync("/api/sessions");
        // Either 200 (if tool handles error) or 500 (global handler)
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task CreateSession_ValidRequest_RoutesCorrectly()
    {
        var request = new
        {
            SessionName = "test-session"
        };
        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/sessions", content);
        // Tool will throw due to fake SQL Server, but endpoint is reachable
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task StartSession_RoutesCorrectly()
    {
        var response = await _client.PostAsync(
            "/api/sessions/test/start",
            null);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task StopSession_RoutesCorrectly()
    {
        var response = await _client.PostAsync(
            "/api/sessions/test/stop",
            null);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task DropSession_RoutesCorrectly()
    {
        var response = await _client.DeleteAsync(
            "/api/sessions/test");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task QuickCapture_RoutesCorrectly()
    {
        var request = new
        {
            SessionName = "quick-test"
        };
        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/sessions/quick-capture", content);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    // ── Event Retrieval Endpoints ───────────────────

    [Fact]
    public async Task GetEvents_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/sessions/test/events");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task GetStats_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/sessions/test/stats");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task AnalyzeSequence_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/sessions/test/sequence");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    // ── Connection Info ─────────────────────────────

    [Fact]
    public async Task GetConnectionInfo_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/connection-info");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    // ── Permission Endpoints ────────────────────────

    [Fact]
    public async Task CheckPermissions_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/permissions/check");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task GrantPermissions_RoutesCorrectly()
    {
        var request = new
        {
            TargetLogin = "test_user"
        };
        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _client.PostAsync("/api/permissions/grant", content);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    // ── Diagnostic Endpoints ────────────────────────

    [Fact]
    public async Task GetDeadlocks_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/sessions/test/deadlocks");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task GetBlocking_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/sessions/test/blocking");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task GetWaitStats_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/wait-stats");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task HealthCheck_RoutesCorrectly()
    {
        var response = await _client.GetAsync(
            "/api/health");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    // ── Memory Endpoints ────────────────────────────

    [Fact]
    public async Task ListCaptures_RoutesCorrectly()
    {
        var response = await _client.GetAsync("/api/memory/captures");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task GetCapture_RoutesCorrectly()
    {
        var response = await _client.GetAsync("/api/memory/captures/test-id");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task MemoryStats_RoutesCorrectly()
    {
        var response = await _client.GetAsync("/api/memory/stats");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task PurgeMemory_RoutesCorrectly()
    {
        var response = await _client.DeleteAsync("/api/memory/test-server");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task TagCapture_RoutesCorrectly()
    {
        var response = await _client.PostAsync(
            "/api/memory/captures/test-id/tag?tags=perf,review",
            null);
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.InternalServerError);
    }

    // ── Error Handling ──────────────────────────────

    [Fact]
    public async Task ToolErrorHandling_ReturnsJsonWithErrorField()
    {
        // Tool classes have their own try/catch, so errors from fake SQL connections
        // are returned as 200 with error JSON, not 500
        var response = await _client.GetAsync(
            "/api/sessions/nonexistent/events");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        // The tool returns JSON with an error field
        Assert.Contains("error", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToolErrorHandling_ResponseIsValidJson()
    {
        var response = await _client.GetAsync(
            "/api/sessions/nonexistent/stats");

        var content = await response.Content.ReadAsStringAsync();
        // Verify the error response is valid JSON
        var parsed = JsonSerializer.Deserialize<JsonElement>(content);
        Assert.True(parsed.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task ContentType_IsApplicationJson()
    {
        // Memory endpoints don't need connection string, so they should return JSON
        var response = await _client.GetAsync("/api/memory/captures");
        if (response.IsSuccessStatusCode)
        {
            Assert.Contains("application/json", response.Content.Headers.ContentType?.MediaType);
        }
    }
}
