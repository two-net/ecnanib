using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.MarketData.Abstractions;
using Ecnanib.MarketData.Services;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.MarketData.Services;

/// <summary>The chart's history (FR-UI-03 c): a tracked series answers from memory, anything else falls through the (down) database to the exchange.</summary>
public class CandleReaderTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 7, 10, 0, 5, TimeSpan.Zero);
    private static readonly MarketKey Eth = new(ExchangeEnvironment.Production, MarketType.Spot, "ETHUSDT");
    private static readonly MarketKey Btc = new(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATrackedSeriesIsServedFromMemoryAndAnythingElseFromTheExchange()
    {
        var time = new FakeTimeProvider(Start);
        var exchange = new FakeExchangeClientFactory
        {
            OnExchangeInfo = (_, _) => FakeExchangeClientFactory.Listing("BTCUSDT", "ETHUSDT"),
            OnRecentCandles = (symbol, interval, count) => Recent(symbol, interval, count, time.GetUtcNow()),
        };
        await using var provider = MarketDataTestHost.BuildServices(logs: new TestLoggerProvider(), config: new() { ["MarketData:BackfillCandles:1H"] = "30" }, time: time, exchange: exchange,
            streams: new FakeMarketStreams(), configure: s => s.AddSingleton<IDbContextFactory<EcnanibDbContext>>(new ThrowingDbContextFactory()));
        var feed = provider.GetRequiredService<CandleFeed>();
        var reader = provider.GetRequiredService<ICandleReader>();
        Assert.IsType<CandleReader>(reader);

        feed.Track(new MarketDataNeeds(Guid.NewGuid(), Eth, CandleInterval.OneHour, null));
        await Wait.UntilAsync(() => feed.Find(Eth, CandleInterval.OneHour)?.IsReady == true, "backfilled");
        var readsAfterBackfill = exchange.RecentCandleReads.Count;

        var fromSeries = await reader.ReadAsync(Eth, CandleInterval.OneHour, 10, Ct);

        Assert.Equal(10, fromSeries.Count);
        Assert.Equal(readsAfterBackfill, exchange.RecentCandleReads.Count);
        Assert.Equal(new DateTimeOffset(2026, 1, 7, 9, 0, 0, TimeSpan.Zero), fromSeries[^1].OpenTime);
        Assert.True(fromSeries.Zip(fromSeries.Skip(1)).All(pair => pair.Second.OpenTime == pair.First.OpenTime.AddHours(1)));
        Assert.All(fromSeries, c => Assert.True(c.IsClosed));

        // Untracked: the database is down here, so the exchange answers; more than the series holds goes there too.
        var untracked = await reader.ReadAsync(Btc, CandleInterval.OneHour, 5, Ct);
        Assert.Equal(5, untracked.Count);
        Assert.Contains(exchange.RecentCandleReads, r => r.Symbol == "BTCUSDT" && r.Interval == CandleInterval.OneHour && r.Count == 5);
        var deeper = await reader.ReadAsync(Eth, CandleInterval.OneHour, 100, Ct);
        Assert.Equal(100, deeper.Count);
        Assert.Contains(exchange.RecentCandleReads, r => r.Symbol == "ETHUSDT" && r.Count == 100);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.ReadAsync(Eth, CandleInterval.OneHour, 0, Ct));
    }

    /// <summary>Contiguous closed candles ending with the last boundary before <paramref name="now"/>.</summary>
    private static IReadOnlyList<Candle> Recent(string symbol, CandleInterval interval, int count, DateTimeOffset now)
    {
        var span = interval.ToTimeSpan();
        var lastOpen = interval.OpenTimeOf(now) - span;
        return Enumerable.Range(0, count).Reverse().Select(i => FakeMarketStreams.ClosedCandle(symbol, interval, lastOpen - i * span, 100m + i)).ToList();
    }
}
