using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Health;
using Ecnanib.Health.Abstractions;
using Ecnanib.Health.Services;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Health.DependencyInjection;

public class AddHealthTests
{
    [Fact]
    public async Task ResolvesTheReportWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        await using var provider = AgentsTestHost.BuildServices(configure: s =>
        {
            s.AddHealth();
            s.AddSingleton<IExchangeHealth>(new FakeExchangeHealth());
            s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory);
        });

        Assert.IsType<SystemHealth>(provider.GetRequiredService<ISystemHealth>());
        Assert.DoesNotContain(provider.GetServices<IHostedService>(), h => h.GetType().Namespace!.StartsWith("Ecnanib.Health", StringComparison.Ordinal));
        Assert.Equal(0, factory.Calls);
    }
}
