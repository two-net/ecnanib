using System.Collections.Concurrent;
using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.MarketData;

/// <summary>
/// FR-UI-05, FR-UI-03, NFR-PERF-05 and EXT-BIN-08: a page's live price and forming candle come from one shared read
/// per pair, from the pair's own Demo or Live server, only while someone watches it, and never during a backoff. The
/// chart's closed candles are the cycles' own, shared and cached.
/// </summary>
public sealed class MarketWatchTests(PostgresFixture postgres)
{
    private static readonly Dictionary<string, string?> Fast = new() { ["Binance:LiveMarketIntervalSeconds"] = "5" };

    private static CancellationToken Cancellation => MarketDataApp.Cancellation;

    public static TheoryData<string> InvalidIntervals => ["4", "61", "often"];

    [Fact]
    public async Task One_read_serves_every_page_watching_a_pair_with_its_forming_candle_and_mark_price()
    {
        await using var app = new MarketDataApp(postgres, Fast);
        var hourly = new Updates();
        var price = new Updates();
        var key = new WatchKey(Market.Futures, TradingEnvironment.Demo, MarketDataApp.Symbol, Timeframe.OneHour);

        using (Watch(app).Watch(key, hourly.Add))
        using (Watch(app).Watch(key with { Timeframe = null }, price.Add))
        {
            var update = await hourly.UntilAsync(update => update is { Stale: false, Forming: not null });
            var priced = await price.UntilAsync(update => update is { Stale: false, MarkPrice: not null });
            var hour = app.LastClose("1h");

            Assert.Equal((60118.75m, 60118.75m, 60118.75m), (update.MarkPrice!.Value, update.Price!.Value, update.ClosingPrice(isLong: false)!.Value));
            Assert.Equal((hour, hour - TimeSpan.FromHours(1)), (update.Forming!.OpenTime, update.LastClosed!.OpenTime));
            Assert.Equal(update.Forming.Close, update.LastPrice);
            Assert.Null(priced.Forming);

            // Each read asks for the price and the candles together, once, however many pages watch.
            Assert.Equal(app.Binance.To("/fapi/v1/premiumIndex").Count, app.Klines("1h").Count);
            Assert.All(app.Binance.Requests, request => Assert.Equal(FakeBinance.FuturesDemo, request.Host));
        }
    }

    [Fact]
    public async Task On_Spot_a_long_closes_at_the_bid_a_short_at_the_ask_and_a_page_that_throws_stops_nobody()
    {
        await using var app = new MarketDataApp(postgres, Fast);
        var updates = new Updates();
        var key = new WatchKey(Market.Spot, TradingEnvironment.Demo, MarketDataApp.Symbol, null);

        using var broken = Watch(app).Watch(key, _ => throw new InvalidOperationException("A page that broke."));
        using var page = Watch(app).Watch(key, updates.Add);
        var update = await updates.UntilAsync(update => update is { Stale: false, BidPrice: not null });

        Assert.Equal(
            (60123.45m, 60123.44m, 60123.46m),
            (update.Price!.Value, update.ClosingPrice(isLong: true)!.Value, update.ClosingPrice(isLong: false)!.Value));
        Assert.Null(update.MarkPrice);
        Assert.All(app.Binance.Requests, request => Assert.Equal(FakeBinance.SpotDemo, request.Host));
    }

    [Fact]
    public async Task Nothing_is_read_once_the_last_page_leaves()
    {
        await using var app = new MarketDataApp(postgres, Fast);
        var updates = new Updates();

        using (Watch(app).Watch(new WatchKey(Market.Spot, TradingEnvironment.Live, MarketDataApp.Symbol, null), updates.Add))
        {
            await updates.UntilAsync(update => !update.Stale);
        }

        var reads = app.Binance.Requests.Count;
        await Task.Delay(TimeSpan.FromSeconds(6), Cancellation);

        Assert.Equal(reads, app.Binance.Requests.Count);
    }

    [Fact]
    public async Task A_throttled_read_shows_the_last_price_as_stale_and_waits_as_long_as_Binance_says()
    {
        await using var app = new MarketDataApp(postgres, Fast);
        app.Binance.On(
            HttpMethod.Get,
            "/fapi/v1/premiumIndex",
            _ => FakeBinance.Error(HttpStatusCode.TooManyRequests, -1003, "Too many requests.", ("Retry-After", "60")));
        var updates = new Updates();

        using var page = Watch(app).Watch(new WatchKey(Market.Futures, TradingEnvironment.Demo, MarketDataApp.Symbol, null), updates.Add);
        await updates.UntilAsync(update => update.Stale);
        await Task.Delay(TimeSpan.FromSeconds(6), Cancellation);

        Assert.Single(app.Binance.To("/fapi/v1/premiumIndex"));
        Assert.All(updates.All, update => Assert.True(update.Stale));
    }

    [Fact]
    public async Task A_chart_reads_the_cycles_own_candles_and_shares_their_fetch()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1h");

        var candles = await app.MarketData.ReadCandlesAsync(
            new CandleRequest(Market.Spot, TradingEnvironment.Live, MarketDataApp.Symbol, Timeframe.OneHour, 120), Cancellation);
        await app.CollectAsync(Timeframe.OneHour, close);

        Assert.Equal(120, candles.Count);
        Assert.Equal(close - TimeSpan.FromHours(1), candles[^1].OpenTime);
        Assert.Single(app.Klines("1h"));

        await Assert.ThrowsAsync<ArgumentException>(() => app.MarketData.ReadCandlesAsync(
            new CandleRequest(Market.Spot, TradingEnvironment.Live, MarketDataApp.Symbol, Timeframe.OneHour, CandleRequest.MaxCount + 1), Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => app.MarketData.ReadCandlesAsync(
            new CandleRequest(Market.Spot, TradingEnvironment.Live, "btc-usdt", Timeframe.OneHour, 10), Cancellation));
        Assert.Throws<ArgumentException>(() => Watch(app).Watch(new WatchKey(Market.Spot, TradingEnvironment.Live, "btc-usdt", null), _ => { }));
    }

    [Theory]
    [MemberData(nameof(InvalidIntervals))]
    public async Task An_invalid_interval_stops_startup_and_is_named(string value)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { ["Binance:LiveMarketIntervalSeconds"] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains("Binance:LiveMarketIntervalSeconds", exception.ToString(), StringComparison.Ordinal);
    }

    private static IMarketWatch Watch(MarketDataApp app) => app.Exchange.App.Services.GetRequiredService<IMarketWatch>();

    /// <summary>What a page was sent, from the watch's loop.</summary>
    private sealed class Updates
    {
        private readonly ConcurrentQueue<LiveMarket> _updates = new();

        public IReadOnlyList<LiveMarket> All => [.. _updates];

        public void Add(LiveMarket update) => _updates.Enqueue(update);

        public async Task<LiveMarket> UntilAsync(Func<LiveMarket, bool> condition)
        {
            await LoggedApp.UntilAsync(() => ValueTask.FromResult(All.Any(condition)));

            return All.Last(condition);
        }
    }
}
