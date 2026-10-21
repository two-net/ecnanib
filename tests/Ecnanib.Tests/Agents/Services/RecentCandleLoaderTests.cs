using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Services.Placeholders;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Tests.Agents.Services;

/// <summary>FR-STR-03 and FR-DATA-04 in the placeholder loader: every declared series is read and aligned to the evaluated candle's close (no look-ahead); references and the mark price are tolerated.</summary>
public class RecentCandleLoaderTests
{
    private static readonly DateTimeOffset ClosedOpen = new(2026, 1, 7, 8, 0, 0, TimeSpan.Zero);   // Wednesday 08:00; cutoff 12:00

    private static readonly MarketKey EthFutures = new(ExchangeEnvironment.Production, MarketType.UsdFutures, "ETHUSDT");

    private static readonly StrategyDescriptor Strategy = new("test-4h", "Test", CandleInterval.FourHours, 1, "test", [],
        [CandleInterval.OneDay, CandleInterval.OneWeek], [new ReferenceSeries("BTCUSDT", CandleInterval.OneWeek)]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LoadsAndAlignsEveryDeclaredSeries()
    {
        var exchange = new FakeExchangeClientFactory { OnRecentCandles = Script };
        var loader = new RecentCandleLoader(exchange, NullLogger<RecentCandleLoader>.Instance);
        var agent = new Agent { Id = Guid.NewGuid(), Name = "x", MarketType = MarketType.UsdFutures, Symbol = "ETHUSDT", Timeframe = CandleInterval.FourHours, StrategyId = "test-4h" };
        var closed = FakeMarketStreams.ClosedCandle("ETHUSDT", CandleInterval.FourHours, ClosedOpen, 101m);

        var snapshot = await loader.LoadAsync(agent, Strategy, EthFutures, closed, Ct);

        Assert.Equal(ClosedOpen, snapshot.Latest!.OpenTime);
        Assert.Equal([new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero)], snapshot.Higher(CandleInterval.OneDay).Select(c => c.OpenTime));
        Assert.Equal([new DateTimeOffset(2025, 12, 22, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 12, 29, 0, 0, 0, TimeSpan.Zero)], snapshot.Higher(CandleInterval.OneWeek).Select(c => c.OpenTime));
        var btc = snapshot.Reference(new ReferenceSeries("BTCUSDT", CandleInterval.OneWeek));
        Assert.Equal(2, btc.Count);
        Assert.All(btc, c => Assert.Equal("BTCUSDT", c.Symbol));
        Assert.Null(snapshot.MarkPrice);
        Assert.Equal(
            [("ETHUSDT", CandleInterval.FourHours), ("ETHUSDT", CandleInterval.OneDay), ("ETHUSDT", CandleInterval.OneWeek), ("BTCUSDT", CandleInterval.OneWeek)],
            exchange.RecentCandleReads.Select(r => (r.Symbol, r.Interval)));
        Assert.All(exchange.RecentCandleReads, r => Assert.Equal(ExchangeEnvironment.Production, r.Environment));

        exchange.OnMarkPrice = symbol => new MarkPrice(symbol, 100.5m, null, 0.0001m, ClosedOpen.AddHours(8), ClosedOpen.AddHours(4));
        var withMark = await loader.LoadAsync(agent, Strategy, EthFutures, closed, Ct);
        Assert.Equal(0.0001m, withMark.MarkPrice!.FundingRate);
    }

    [Fact]
    public async Task ABtcAgentReusesItsOwnWeeklySeriesAndAReferenceFailureIsTolerated()
    {
        var exchange = new FakeExchangeClientFactory { OnRecentCandles = Script };
        var loader = new RecentCandleLoader(exchange, NullLogger<RecentCandleLoader>.Instance);
        var btcAgent = new Agent { Id = Guid.NewGuid(), Name = "x", MarketType = MarketType.Spot, Symbol = "BTCUSDT", Timeframe = CandleInterval.FourHours, StrategyId = "test-4h" };
        var closed = FakeMarketStreams.ClosedCandle("BTCUSDT", CandleInterval.FourHours, ClosedOpen, 101m);

        var snapshot = await loader.LoadAsync(btcAgent, Strategy, new MarketKey(ExchangeEnvironment.Production, MarketType.Spot, "BTCUSDT"), closed, Ct);

        Assert.Equal(3, exchange.RecentCandleReads.Count);
        Assert.Same(snapshot.Higher(CandleInterval.OneWeek), snapshot.Reference(new ReferenceSeries("BTCUSDT", CandleInterval.OneWeek)));
        Assert.Null(snapshot.MarkPrice);

        exchange.RecentCandleReads.Clear();
        exchange.OnRecentCandles = (symbol, interval, count) => symbol == "BTCUSDT" ? throw new TimeoutException("binance down") : Script(symbol, interval, count);
        var ethAgent = new Agent { Id = Guid.NewGuid(), Name = "x", MarketType = MarketType.UsdFutures, Symbol = "ETHUSDT", Timeframe = CandleInterval.FourHours, StrategyId = "test-4h" };
        var tolerant = await loader.LoadAsync(ethAgent, Strategy, EthFutures, FakeMarketStreams.ClosedCandle("ETHUSDT", CandleInterval.FourHours, ClosedOpen, 101m), Ct);
        Assert.Empty(tolerant.Reference(new ReferenceSeries("BTCUSDT", CandleInterval.OneWeek)));
        Assert.Equal(2, tolerant.Higher(CandleInterval.OneWeek).Count);

        exchange.OnRecentCandles = (symbol, interval, count) => interval == CandleInterval.OneDay ? throw new TimeoutException("binance down") : Script(symbol, interval, count);
        await Assert.ThrowsAsync<TimeoutException>(() => loader.LoadAsync(ethAgent, Strategy, EthFutures, FakeMarketStreams.ClosedCandle("ETHUSDT", CandleInterval.FourHours, ClosedOpen, 101m), Ct));
    }

    /// <summary>Closed candles ending with one that is still forming at the cutoff: the exchange answers everything closed by the wall clock.</summary>
    private static IReadOnlyList<Candle> Script(string symbol, CandleInterval interval, int count)
    {
        var last = interval switch
        {
            CandleInterval.FourHours => ClosedOpen,
            CandleInterval.OneDay => new DateTimeOffset(2026, 1, 7, 0, 0, 0, TimeSpan.Zero),
            CandleInterval.OneWeek => new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero),
            _ => throw new NotSupportedException(),
        };
        return Enumerable.Range(0, Math.Min(count, 3)).Reverse()
            .Select(i => FakeMarketStreams.ClosedCandle(symbol, interval, last - i * interval.ToTimeSpan(), 100m))
            .ToList();
    }
}
