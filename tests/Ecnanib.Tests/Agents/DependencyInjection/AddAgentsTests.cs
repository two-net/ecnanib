using Ecnanib.Agents;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services;
using Ecnanib.Agents.Services.Paper;
using Ecnanib.Agents.Services.Placeholders;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.Strategies;
using Ecnanib.Strategies.Catalog;
using Ecnanib.Strategies.Engine;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

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
        Assert.IsType<StrategyEngine>(provider.GetRequiredService<IStrategyEngine>());
        Assert.IsType<AgentEvents>(provider.GetRequiredService<IAgentEvents>());
        Assert.IsType<AgentKeyDependency>(provider.GetRequiredService<IKeyDependentAgents>());
        Assert.IsType<RecentCandleLoader>(provider.GetRequiredService<IMarketDataLoader>());
        Assert.IsType<UncachedSymbolCatalog>(provider.GetRequiredService<ISymbolCatalog>());
        Assert.IsType<PendingCouncil>(provider.GetRequiredService<ICouncil>());
        Assert.IsType<PendingExecutionService>(provider.GetRequiredService<IExecutionService>());
        Assert.IsType<NoExperienceTracker>(provider.GetRequiredService<IExperienceTracker>());
        Assert.IsType<NoCouncilSuggestions>(provider.GetRequiredService<ICouncilSuggestions>());
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
    public async Task AStageRegisteredFirstReplacesThePlaceholderAndTheStrategiesEngine()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Database.DatabaseTestHost.OfflineConfiguration()).Build();
        var engine = new FakeStrategyEngine();
        var loader = new FakeMarketDataLoader();
        var tracker = new FakeExperienceTracker();
        var suggestions = new FakeCouncilSuggestions();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        services.AddSingleton<IStrategyEngine>(engine);
        services.AddSingleton<IMarketDataLoader>(loader);
        services.AddSingleton<IExperienceTracker>(tracker);
        services.AddSingleton<ICouncilSuggestions>(suggestions);
        services.AddStrategies();
        services.AddAgents(configuration);
        await using var provider = services.BuildServiceProvider();

        Assert.Same(engine, provider.GetRequiredService<IStrategyEngine>());
        Assert.Same(loader, provider.GetRequiredService<IMarketDataLoader>());
        Assert.Same(tracker, provider.GetRequiredService<IExperienceTracker>());
        Assert.Same(suggestions, provider.GetRequiredService<ICouncilSuggestions>());
        Assert.IsType<StrategyCatalog>(provider.GetRequiredService<IStrategyCatalog>());
    }

    [Fact]
    public void AddAgentsNeedsTheStrategiesModuleFirst()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Database.DatabaseTestHost.OfflineConfiguration()).Build();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddAgents(configuration));

        Assert.Contains("AddStrategies", ex.Message);
    }

    [Fact]
    public async Task TheRuntimeKeyHandlingSurvivesAddKeysRegisteredAfterIt()
    {
        await using var provider = AgentsTestHost.BuildServices();

        Assert.IsType<AgentKeyDependency>(provider.GetRequiredService<IKeyDependentAgents>());
    }

    private sealed class FakeStrategyEngine : IStrategyEngine
    {
        public StrategyOutput Evaluate(StrategyContext context) => StrategyOutput.Nothing(context.Strategy, "fake");
    }

    private sealed class FakeMarketDataLoader : IMarketDataLoader
    {
        public void Track(MarketDataNeeds needs)
        {
        }

        public void Untrack(Guid agentId)
        {
        }

        public Task<MarketSnapshot> LoadAsync(Agent agent, StrategyDescriptor strategy, MarketKey key, Candle closed, CancellationToken cancellationToken) =>
            Task.FromResult(new MarketSnapshot([closed], new Dictionary<CandleInterval, IReadOnlyList<Candle>>(), new Dictionary<ReferenceSeries, IReadOnlyList<Candle>>(), null));
    }

    private sealed class FakeExperienceTracker : IExperienceTracker
    {
        public Task TrackAsync(ExperienceContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCouncilSuggestions : ICouncilSuggestions
    {
        public Task<IReadOnlyList<CouncilSuggestion>> SuggestAsync(Guid userId, CandleInterval timeframe, MarketType marketType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CouncilSuggestion>>([]);
    }
}
