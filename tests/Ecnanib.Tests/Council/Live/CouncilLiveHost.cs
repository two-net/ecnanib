using Ecnanib.Tests.Agents.Live;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Council.Live;

/// <summary>The application's modules with <c>AddCouncil</c> before <c>AddAgents</c>, against the fixture's database, with a scripted AI fake.</summary>
internal static class CouncilLiveHost
{
    public static (ServiceProvider Provider, TestLoggerProvider Logs, FakeTimeProvider Time, FakeExchangeClientFactory Exchange, FakeMarketStreams Streams) Build(
        DatabaseLiveFixture fixture, FakeAiClientFactory ai, Dictionary<string, string?>? config = null, Action<IServiceCollection>? configure = null, DateTimeOffset? now = null, bool execution = false)
    {
        var logs = new TestLoggerProvider();
        var time = new FakeTimeProvider(now ?? AgentsLiveSeed.Now);
        var exchange = new FakeExchangeClientFactory
        {
            OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT", "ETHUSDT"),
            OnRecentCandles = (symbol, interval, count) => AgentsLiveSeed.RecentCandles(symbol, interval, count, time.GetUtcNow()),
        };
        var streams = new FakeMarketStreams();
        var provider = CouncilTestHost.BuildServices(fixture.ConnectionString, logs, config, time, exchange, ai, streams, configure, execution);
        return (provider, logs, time, exchange, streams);
    }
}
