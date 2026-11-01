using System.Net;
using Ecnanib.Modules.Administration.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Smoke;

public sealed class AppStartupTests(SharedApp factory) : IClassFixture<SharedApp>
{
    [Fact]
    public async Task Home_page_is_served()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void A_module_contract_resolves_to_its_internal_implementation()
    {
        var systemInfo = factory.Services.GetRequiredService<ISystemInfo>();

        Assert.NotEmpty(systemInfo.Version);
        Assert.True(systemInfo.Uptime > TimeSpan.Zero);
    }
}
