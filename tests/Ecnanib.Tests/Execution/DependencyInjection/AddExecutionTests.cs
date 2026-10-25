using Ecnanib.Agents;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Execution;
using Ecnanib.Execution.Abstractions;
using Ecnanib.Execution.Services;
using Ecnanib.Execution.Services.Live;
using Ecnanib.Strategies;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecnanib.Tests.Execution.DependencyInjection;

public class AddExecutionTests
{
    [Fact]
    public async Task RegisteredBeforeAddAgentsTheModuleReplacesTheFailSafeDefaultsWithoutConnecting()
    {
        var factory = new ThrowingDbContextFactory();
        var exchange = new FakeExchangeClientFactory();
        var streams = new FakeMarketStreams();
        await using var provider = AgentsTestHost.BuildServices(exchange: exchange, streams: streams, execution: true, configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(factory));

        Assert.IsType<ExecutionService>(provider.GetRequiredService<IExecutionService>());
        var venues = provider.GetRequiredService<IOrderVenues>();
        Assert.IsType<LiveOrderVenue>(venues.For(AgentMode.Live));
        Assert.Equal(AgentMode.Paper, venues.For(AgentMode.Paper).Mode);
        var hosted = provider.GetServices<IHostedService>().Where(h => h.GetType().Namespace!.StartsWith("Ecnanib.Execution", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, hosted.Count);
        Assert.Contains(hosted, h => ReferenceEquals(h, provider.GetRequiredService<LiveFillTracker>()));
        Assert.Contains(hosted, h => ReferenceEquals(h, provider.GetRequiredService<ExchangeReconciler>()));
        Assert.NotNull(provider.GetRequiredService<IDailyLossLimit>());
        Assert.NotNull(provider.GetRequiredService<IAgentGates>());
        Assert.IsType<AgentPerformance>(provider.GetRequiredService<IAgentPerformance>());
        Assert.IsType<TradeTimelineReader>(provider.GetRequiredService<ITradeTimelineReader>());

        Assert.Equal(0, factory.Calls);
        Assert.Empty(exchange.ExchangeInfoReads);
        Assert.Equal(0, streams.CandleSubscriptionsEver);
    }

    [Fact]
    public void AddExecutionRefusesToComeAfterAddAgents()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:ConnectionString"] = Database.DatabaseTestHost.OfflineConnectionString }).Build();
        var services = new ServiceCollection();
        services.AddDatabase(configuration);
        services.AddStrategies();
        services.AddAgents(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddExecution(configuration));

        Assert.Contains("before AddAgents", ex.Message);
    }
}
