using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.MarketData;

/// <summary>FR-CYC-09, NFR-PERF-05: public data is fetched once per symbol, timeframe and candle, and shared.</summary>
public sealed class SharingTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    [Fact]
    public async Task Agents_on_one_symbol_share_one_fetch_per_timeframe_and_candle()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1h");

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => app.CollectAsync(Timeframe.OneHour, close)));

        // A cold cache asks for the whole window, from the market-data server for Spot Live.
        var hourly = Assert.Single(app.Klines("1h"));
        Assert.Equal(
            (Ms(close - (Hour * CandleSeries.Window)), Ms(close - Hour), "500"),
            (hourly.Parameter("startTime"), hourly.Parameter("endTime"), hourly.Parameter("limit")));
        Assert.Single(app.Klines("1d"));
        Assert.Single(app.Binance.To("/api/v3/ticker/24hr"));
        Assert.All(app.Binance.Requests, request => Assert.Equal(FakeBinance.MarketData, request.Host));

        var snapshot = snapshots[0];
        Assert.All(snapshots, other => Assert.Same(snapshot.Candles, other.Candles));
        Assert.Equal((Hour, close - Hour, CandleSeries.Window), (snapshot.Candles.Interval, snapshot.Candles.LastOpenTime, snapshot.Candles.Candles.Count));
        Assert.Equal(close - Hour, snapshot.Candles.Candles[^1].OpenTime);
        Assert.Equal(BinanceSamples.Floor("1d", close) - TimeSpan.FromDays(1), snapshot.HigherTimeframe.Candles[^1].OpenTime);
        Assert.Equal(TimeSpan.FromDays(1), snapshot.HigherTimeframe.Interval);
        Assert.NotNull(snapshot.Candles.Indicators.Ema200);
        Assert.NotNull(snapshot.HigherTimeframe.Indicators.Ema200);
        Assert.Equal(new Quote(60123.45m, 60123.44m, 1.25m, 60123.46m, 0.75m, snapshot.Quote.At), snapshot.Quote);
        Assert.Null(snapshot.Futures);
    }

    [Fact]
    public async Task The_higher_timeframe_is_fetched_once_per_its_own_candle_and_later_candles_fetch_only_what_is_new()
    {
        await using var app = new MarketDataApp(postgres);
        var hour = app.LastClose("1h") - (2 * Hour);

        foreach (var minutes in new[] { 15, 30, 45, 60 })
        {
            var snapshot = await app.CollectAsync(Timeframe.FifteenMinutes, hour + TimeSpan.FromMinutes(minutes));
            Assert.Equal(hour + TimeSpan.FromMinutes(minutes) - Quarter, snapshot.Candles.Candles[^1].OpenTime);
            Assert.Equal(CandleSeries.Window, snapshot.Candles.Candles.Count);
        }

        // The whole window once, and then each new candle on its own.
        Assert.Equal(
            [
                (Ms(hour - (Quarter * (CandleSeries.Window - 1))), "500"),
                (Ms(hour + Quarter), "1"),
                (Ms(hour + (2 * Quarter)), "1"),
                (Ms(hour + (3 * Quarter)), "1"),
            ],
            app.Klines("15m").Select(request => (request.Parameter("startTime"), request.Parameter("limit"))));

        // 1H is the context for 15m (SRS A.1). Its candle closed only once in that hour.
        var hours = app.Klines("1h");
        Assert.Equal(2, hours.Count);
        Assert.Equal((Ms(hour), "1"), (hours[1].Parameter("startTime"), hours[1].Parameter("limit")));
    }

    [Fact]
    public async Task A_daily_agent_gets_weekly_context_aligned_to_Monday()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1d");

        var snapshot = await app.CollectAsync(Timeframe.OneDay, close);

        var week = snapshot.HigherTimeframe;
        Assert.Equal(TimeSpan.FromDays(7), week.Interval);
        Assert.Equal(BinanceSamples.Floor("1w", close) - TimeSpan.FromDays(7), week.LastOpenTime);
        Assert.Equal(DayOfWeek.Monday, week.Candles[^1].OpenTime.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, week.Candles[^1].OpenTime.TimeOfDay);
        Assert.Single(app.Klines("1w"));
    }

    [Fact]
    public async Task Markets_and_environments_are_never_shared()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1h");

        await app.CollectAsync(Timeframe.OneHour, close, Market.Spot, TradingEnvironment.Live);
        await app.CollectAsync(Timeframe.OneHour, close, Market.Spot, TradingEnvironment.Demo);
        var futures = await app.CollectAsync(Timeframe.OneHour, close, Market.Futures, TradingEnvironment.Demo);

        Assert.Equal(
            [(FakeBinance.MarketData, "/api/v3/klines"), (FakeBinance.SpotDemo, "/api/v3/klines"), (FakeBinance.FuturesDemo, "/fapi/v1/klines")],
            app.Klines("1h").Select(request => (request.Host, request.Path)));

        Assert.Equal(new Quote(60120.10m, 60120.10m, 3.512m, 60120.20m, 0.041m, futures.Quote.At), futures.Quote);
        Assert.Equal(
            new FuturesData(60118.75m, 60131.0234m, 0.0001m, DateTimeOffset.FromUnixTimeMilliseconds(1793808000000), futures.Futures!.At),
            futures.Futures);
    }

    [Fact]
    public async Task After_a_restart_the_cache_answers_and_only_new_candles_are_fetched()
    {
        var close = DateTimeOffset.MinValue;
        string connectionString;

        await using (var first = new MarketDataApp(postgres))
        {
            close = first.LastClose("1h") - Hour;
            connectionString = first.ConnectionString;
            await first.CollectAsync(Timeframe.OneHour, close);
        }

        await using var second = new MarketDataApp(postgres, connectionString: connectionString);

        var same = await second.CollectAsync(Timeframe.OneHour, close);
        Assert.Empty(second.Klines("1h"));
        Assert.Empty(second.Klines("1d"));
        Assert.Equal(CandleSeries.Window, same.Candles.Candles.Count);

        var next = await second.CollectAsync(Timeframe.OneHour, close + Hour);
        var tail = Assert.Single(second.Klines("1h"));
        Assert.Equal((Ms(close), Ms(close), "1"), (tail.Parameter("startTime"), tail.Parameter("endTime"), tail.Parameter("limit")));
        Assert.Equal((close, CandleSeries.Window), (next.Candles.Candles[^1].OpenTime, next.Candles.Candles.Count));
        Assert.Equal(same.Candles.Candles.Skip(1), next.Candles.Candles.SkipLast(1));
    }

    [Fact]
    public async Task A_candle_that_is_still_open_is_never_kept()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1h");

        // As though Binance ignored endTime, and sent the candle that opened at the close and is still open.
        app.Binance.On(HttpMethod.Get, "/api/v3/klines", request =>
        {
            var limit = int.Parse(request.Parameter("limit")!, CultureInfo.InvariantCulture) + 1;
            var query = string.Join('&', request.Parameters.Where(parameter => parameter.Name is not ("endTime" or "limit")).Select(parameter => $"{parameter.Name}={parameter.Value}"));
            var widened = request with { Uri = new Uri($"{request.Uri.GetLeftPart(UriPartial.Path)}?{query}&limit={limit}") };

            return FakeBinance.Json(BinanceSamples.Klines(widened, app.Clock.GetUtcNow()));
        });

        var snapshot = await app.CollectAsync(Timeframe.OneHour, close);

        Assert.Equal(close - Hour, snapshot.Candles.Candles[^1].OpenTime);
        Assert.Equal(
            (0L, (long)CandleSeries.Window),
            (await Sql.ScalarAsync<long>(app.ConnectionString, $"SELECT count(*) FROM candle_cache WHERE interval = 'OneHour' AND open_time >= '{close:O}'", MarketDataApp.Cancellation),
                await Sql.ScalarAsync<long>(app.ConnectionString, "SELECT count(*) FROM candle_cache WHERE interval = 'OneHour'", MarketDataApp.Cancellation)));
    }

    [Fact]
    public async Task A_candle_Binance_does_not_have_is_not_asked_for_again_and_again()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1h") - Hour;
        app.AnswerKlines(missing: open => open == close - Hour);

        var first = await app.CollectAsync(Timeframe.OneHour, close);
        await app.CollectAsync(Timeframe.OneHour, close);
        await app.CollectAsync(Timeframe.OneHour, close);

        Assert.Single(app.Klines("1h"));
        Assert.Equal((close - Hour, close - (2 * Hour)), (first.Candles.LastOpenTime, first.Candles.Candles[^1].OpenTime));

        // The next candle asks for both, once.
        var next = await app.CollectAsync(Timeframe.OneHour, close + Hour);
        await app.CollectAsync(Timeframe.OneHour, close + Hour);

        Assert.Equal(2, app.Klines("1h").Count);
        Assert.Equal((Ms(close - Hour), "2"), (app.Klines("1h")[1].Parameter("startTime"), app.Klines("1h")[1].Parameter("limit")));
        Assert.Equal(close, next.Candles.Candles[^1].OpenTime);
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_does_not_stop_the_fetch_for_the_others()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1h");
        var arrived = 0;
        var bothArrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        app.Binance.On(HttpMethod.Get, "/api/v3/klines", async (request, _) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                bothArrived.SetResult();
            }

            await release.Task;

            return FakeBinance.Json(BinanceSamples.Klines(request, app.Clock.GetUtcNow()));
        });

        using var leaving = new CancellationTokenSource();
        var leaver = app.CollectAsync(Timeframe.OneHour, close, cancellationToken: leaving.Token);
        var stayer = app.CollectAsync(Timeframe.OneHour, close);
        await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), MarketDataApp.Cancellation);

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaver);
        release.SetResult();

        Assert.Equal(CandleSeries.Window, (await stayer).Candles.Candles.Count);
        Assert.Single(app.Klines("1h"));
    }

    [Fact]
    public async Task A_quote_is_shared_within_a_minute_of_the_close_and_fetched_again_for_the_next_close()
    {
        await using var app = new MarketDataApp(postgres);

        // Ten seconds before a quarter-hour closes.
        var boundary = app.LastClose("15m") + Quarter;
        app.Clock.Advance(boundary - app.Clock.GetUtcNow() - TimeSpan.FromSeconds(10));

        await app.CollectAsync(Timeframe.FifteenMinutes, boundary - Quarter);
        await app.CollectAsync(Timeframe.FifteenMinutes, boundary - Quarter);
        Assert.Single(app.Binance.To("/api/v3/ticker/24hr"));

        // Twenty seconds old, but from before this candle closed.
        app.Clock.Advance(TimeSpan.FromSeconds(20));
        await app.CollectAsync(Timeframe.FifteenMinutes, boundary);
        await app.CollectAsync(Timeframe.FifteenMinutes, boundary);
        Assert.Equal(2, app.Binance.To("/api/v3/ticker/24hr").Count);

        // From after the close, but more than a minute old.
        app.Clock.Advance(TimeSpan.FromSeconds(61));
        await app.CollectAsync(Timeframe.FifteenMinutes, boundary);
        Assert.Equal(3, app.Binance.To("/api/v3/ticker/24hr").Count);
    }

    [Fact]
    public async Task A_Futures_contract_without_funding_has_none()
    {
        await using var app = new MarketDataApp(postgres);
        app.Binance.On(HttpMethod.Get, "/fapi/v1/premiumIndex", _ => FakeBinance.Json(BinanceSamples.PremiumIndex(funding: false)));

        var snapshot = await app.CollectAsync(Timeframe.OneHour, app.LastClose("1h"), Market.Futures, TradingEnvironment.Live);

        Assert.Equal((60118.75m, (decimal?)null, (DateTimeOffset?)null), (snapshot.Futures!.MarkPrice, snapshot.Futures.FundingRate, snapshot.Futures.NextFundingTime));
        Assert.All(app.Binance.Requests, request => Assert.Equal(FakeBinance.FuturesLive, request.Host));
    }

    /// <summary>
    /// Binance's adjusted funding intervals are one list for every symbol, so the cycles that close a candle together
    /// share one call per environment. A symbol it doesn't list is on the default 8 hours.
    /// </summary>
    [Fact]
    public async Task Funding_intervals_are_fetched_once_per_environment_and_close_whatever_the_symbols()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1h");

        var bitcoin = await app.CollectAsync(Timeframe.OneHour, close, Market.Futures, TradingEnvironment.Live);
        var ether = await app.MarketData.CollectAsync(
            new MarketDataRequest(Market.Futures, TradingEnvironment.Live, "ETHUSDT", Timeframe.OneHour, close), MarketDataApp.Cancellation);
        await app.CollectAsync(Timeframe.OneHour, close, Market.Spot, TradingEnvironment.Live);

        Assert.Equal((8, 4), (bitcoin.Futures!.FundingIntervalHours, ether.Futures!.FundingIntervalHours));
        Assert.Equal([FakeBinance.FuturesLive], app.Binance.To("/fapi/v1/fundingInfo").Select(request => request.Host));

        await app.CollectAsync(Timeframe.OneHour, close, Market.Futures, TradingEnvironment.Demo);
        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.CollectAsync(Timeframe.OneHour, app.LastClose("1h"), Market.Futures, TradingEnvironment.Live);

        Assert.Equal(
            [FakeBinance.FuturesLive, FakeBinance.FuturesDemo, FakeBinance.FuturesLive],
            app.Binance.To("/fapi/v1/fundingInfo").Select(request => request.Host));
    }

    internal static string Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
}
