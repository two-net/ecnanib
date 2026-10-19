using Ecnanib.Agents;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Agents.Services.Paper;
using Ecnanib.Agents.Services.Placeholders;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Agents.DependencyInjection;

public class AddAgentsTests
{
    [Fact]
    public async Task ResolvesTheModuleWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        var exchange = new FakeExchangeClientFactory();
        var streams = new FakeMarketStreams();
        await using var provider = AgentsTestHost.BuildServices(exchange: exchange, streams: streams, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));

        Assert.IsType<AgentService>(provider.GetRequiredService<IAgentService>());
        Assert.IsType<StrategyCatalog>(provider.GetRequiredService<IStrategyCatalog>());
        Assert.IsType<AgentEvents>(provider.GetRequiredService<IAgentEvents>());
        Assert.IsType<AgentKeyDependency>(provider.GetRequiredService<IKeyDependentAgents>());
        Assert.IsType<RecentCandleLoader>(provider.GetRequiredService<IMarketDataLoader>());
        Assert.IsType<PendingStrategyEngine>(provider.GetRequiredService<IStrategyEngine>());
        Assert.IsType<PendingCouncil>(provider.GetRequiredService<ICouncil>());
        Assert.IsType<PendingExecutionService>(provider.GetRequiredService<IExecutionService>());
        var venues = provider.GetRequiredService<IOrderVenues>();
        Assert.IsType<PaperOrderVenue>(venues.For(AgentMode.Paper));
        Assert.IsType<PendingLiveOrderVenue>(venues.For(AgentMode.Live));
        Assert.Same(provider.GetRequiredService<AgentScheduler>(), provider.GetRequiredService<IAgentRuntime>());

        var hosted = provider.GetServices<IHostedService>().Where(h => h.GetType().Namespace!.StartsWith("Ecnanib.Agents", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, hosted.Count);
        Assert.Contains(hosted, h => ReferenceEquals(h, provider.GetRequiredService<AgentScheduler>()));
        Assert.Contains(hosted, h => ReferenceEquals(h, provider.GetRequiredService<PaperFillSimulator>()));

        Assert.Equal(0, factory.Calls);
        Assert.Empty(exchange.ExchangeInfoReads);
        Assert.Equal(0, streams.CandleSubscriptionsEver);
    }

    [Fact]
    public async Task AStageRegisteredFirstReplacesThePlaceholder()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = Database.DatabaseTestHost.OfflineConnectionString }).Build();
        var engine = new FakeStrategyEngine();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        services.AddSingleton<IStrategyEngine>(engine);
        services.AddAgents(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.Same(engine, provider.GetRequiredService<IStrategyEngine>());
    }

    [Fact]
    public async Task TheRuntimeKeyHandlingSurvivesAddKeysRegisteredAfterIt()
    {
        await using var provider = AgentsTestHost.BuildServices();

        Assert.IsType<AgentKeyDependency>(provider.GetRequiredService<IKeyDependentAgents>());
    }

    private sealed class FakeStrategyEngine : IStrategyEngine
    {
        public StrategyOutput Evaluate(StrategyContext context) => StrategyOutput.Nothing("fake");
    }
}
