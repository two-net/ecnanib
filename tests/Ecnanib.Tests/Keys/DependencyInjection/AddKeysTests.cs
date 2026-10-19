using Ecnanib.Database;
using Ecnanib.Keys;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Keys.Services;
using Ecnanib.ModelSync;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Keys.DependencyInjection;

public class AddKeysTests
{
    [Fact]
    public async Task ResolvesTheModuleWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        var exchange = new FakeExchangeClientFactory();
        var ai = new FakeAiClientFactory();
        await using var provider = KeysTestHost.BuildServices(exchange: exchange, ai: ai, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));

        Assert.IsType<KeyService>(provider.GetRequiredService<IKeyService>());
        Assert.IsType<KeyResolver>(provider.GetRequiredService<IKeyResolver>());
        Assert.IsType<AgentKeyTransitions>(provider.GetRequiredService<IKeyDependentAgents>());
        Assert.NotNull(provider.GetRequiredService<ExchangeKeyValidator>());
        Assert.NotNull(provider.GetRequiredService<ProviderKeyValidator>());
        Assert.NotNull(provider.GetRequiredService<StoredKeyRevalidation>());

        var hosted = provider.GetServices<IHostedService>().Where(h => h.GetType().Namespace!.StartsWith("Ecnanib.Keys", StringComparison.Ordinal)).ToList();
        Assert.IsType<KeyRevalidator>(Assert.Single(hosted));
        Assert.Same(provider.GetRequiredService<KeyRevalidator>(), hosted[0]);

        Assert.Equal(0, factory.Calls);
        Assert.Empty(exchange.Pings);
        Assert.Empty(ai.Listings);
    }

    [Fact]
    public async Task AnAgentsImplementationRegisteredFirstIsKept()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = DatabaseTestHost.OfflineConnectionString }).Build();
        var agents = new FakeKeyDependentAgents();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        services.AddSingleton<IKeyDependentAgents>(agents);
        services.AddModelSync(configuration);
        services.AddKeys(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.Same(agents, provider.GetRequiredService<IKeyDependentAgents>());
    }
}
