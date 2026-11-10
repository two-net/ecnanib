using System.Net;
using System.Net.Http.Json;
using Ecnanib.AiProviderGateway;
using Ecnanib.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests;

/// <summary>
/// The system is one deployable application: a single host serves the UI and the API and holds
/// every component's services (NFR-ARC-1).
/// </summary>
public class SingleProcessTests(WebApplicationFactory<Program> application) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task One_host_serves_the_UI_and_the_API()
    {
        using var client = application.CreateClient();

        using var page = await client.GetAsync("/", TestContext.Current.CancellationToken);
        using var health = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<h1>Hello, world!</h1>", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var answer = await health.Content.ReadFromJsonAsync<HealthResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(new HealthResponse("ok"), answer);
    }

    [Fact]
    public void The_same_host_holds_the_components_services()
    {
        Assert.NotNull(application.Services.GetRequiredService<IAiProviderGateway>());
    }
}
