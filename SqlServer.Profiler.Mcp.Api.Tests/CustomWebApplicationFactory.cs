using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SqlServer.Profiler.Mcp.Services;

namespace SqlServer.Profiler.Mcp.Api.Tests;

public class CustomWebApplicationFactory : WebApplicationFactory<global::Program>
{
    public Mock<IProfilerService> MockProfilerService { get; } = new();
    public Mock<IQueryFingerprintService> MockFingerprintService { get; } = new();
    public Mock<IWaitStatsService> MockWaitStatsService { get; } = new();
    public Mock<IMemoryService> MockMemoryService { get; } = new();
    public Mock<IEventStreamingService> MockStreamingService { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Remove the real service registrations
            var descriptorsToRemove = services
                .Where(d =>
                    d.ServiceType == typeof(IProfilerService) ||
                    d.ServiceType == typeof(IQueryFingerprintService) ||
                    d.ServiceType == typeof(IWaitStatsService) ||
                    d.ServiceType == typeof(IMemoryService) ||
                    d.ServiceType == typeof(IEventStreamingService) ||
                    d.ServiceType == typeof(EventStreamingService) ||
                    (d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                     (d.ImplementationType == typeof(EventStreamingService) ||
                      d.ImplementationType == typeof(MemoryService) ||
                      d.ImplementationFactory != null)))
                .ToList();

            foreach (var d in descriptorsToRemove)
                services.Remove(d);

            // Remove HostedService registrations that reference real services
            var hostedDescriptors = services
                .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                .ToList();
            foreach (var d in hostedDescriptors)
                services.Remove(d);

            // Register mocks
            services.AddSingleton(MockProfilerService.Object);
            services.AddSingleton(MockFingerprintService.Object);
            services.AddSingleton(MockWaitStatsService.Object);
            services.AddSingleton(MockMemoryService.Object);
            services.AddSingleton(MockStreamingService.Object);
        });
    }
}
