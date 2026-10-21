using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Configuration;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Configuration;
using Ecnanib.MarketData.Services;
using Ecnanib.Tests.Exchange.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.MarketData.Live;

/// <summary>A keyless production backfill (EX-06, FR-DATA-02): the configured depth of closed, contiguous candles in one read. Costs a few request-weight units.</summary>
[Trait("Category", "Live")]
public class CandleFeedPublicLiveTests
{
    [Fact]
    public async Task ARealBackfillHoldsTheConfiguredDepthOfClosedContiguousCandles()
    {
        LiveTestKeys.RequirePublicProduction();
        await using var services = LiveTestKeys.BuildServices();
        var exchange = services.GetRequiredService<IExchangeClientFactory>();
        var streams = new FakeMarketStreams();
        using var feed = new CandleFeed(exchange, streams, new FuturesMetrics(streams, NullLogger<FuturesMetrics>.Instance), new CandleStore(new ThrowingDbContextFactory()),
            Options.Create(new MarketDataOptions()), Options.Create(new RetentionOptions()), TimeProvider.System, NullLogger<CandleFeed>.Instance);
        var key = new MarketKey(ExchangeEnvironment.Production, MarketType.UsdFutures, "BTCUSDT");

        feed.Track(new MarketDataNeeds(Guid.NewGuid(), key, CandleInterval.OneHour, null));
        var series = feed.Find(key, CandleInterval.OneHour)!;
        await Wait.UntilAsync(() => series.IsReady, "backfilled", timeoutMs: 30000);

        Assert.Equal(500, series.Count);
        var candles = series.Window(DateTimeOffset.UtcNow, 500);
        Assert.Equal(500, candles.Count);
        Assert.All(candles, c => Assert.True(c.IsClosed));
        Assert.True(candles[^1].CloseTime < DateTimeOffset.UtcNow);
        for (var i = 1; i < candles.Count; i++)
            Assert.Equal(candles[i - 1].OpenTime + TimeSpan.FromHours(1), candles[i].OpenTime);
        Assert.Equal(1, streams.MarkSubscriptionsEver);
    }
}
