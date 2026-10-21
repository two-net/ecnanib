using Ecnanib.Agents.Abstractions;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Database;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.MarketData;

/// <summary>The agents host with the market-data module registered before <c>AddAgents</c>, as the application does.</summary>
internal static class MarketDataTestHost
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static ServiceProvider BuildServices(
        string? connectionString = DatabaseTestHost.OfflineConnectionString,
        TestLoggerProvider? logs = null,
        Dictionary<string, string?>? config = null,
        TimeProvider? time = null,
        FakeExchangeClientFactory? exchange = null,
        FakeMarketStreams? streams = null,
        Action<IServiceCollection>? configure = null,
        IStrategyEngine? engine = null) =>
        AgentsTestHost.BuildServices(connectionString, logs, config, time, exchange, null, streams, configure, engine, marketData: true);
}
