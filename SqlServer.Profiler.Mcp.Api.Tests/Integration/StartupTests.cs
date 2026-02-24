using System.Net;

namespace SqlServer.Profiler.Mcp.Api.Tests.Integration;

public class StartupTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public StartupTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task App_StartsSuccessfully()
    {
        // If we got here, the app started without throwing
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Swagger_AvailableInDevelopment()
    {
        var response = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("SQL Sentinel", content);
    }

    [Fact]
    public async Task SwaggerUI_AvailableAtRoot()
    {
        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("swagger", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_Returns404_ForUnknownRoute()
    {
        var response = await _client.GetAsync("/api/nonexistent");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
