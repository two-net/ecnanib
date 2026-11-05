using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.MarketData;

/// <summary>What a cycle sees when market data can't be had, in Market Data's own terms.</summary>
public sealed class FailureTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_refused_request_says_when_to_try_again()
    {
        await using var app = new MarketDataApp(postgres);
        app.Binance.On(HttpMethod.Get, "/api/v3/klines", _ => FakeBinance.Error((HttpStatusCode)429, -1003, "Too much request weight used.", ("Retry-After", "7")));

        var failure = await Assert.ThrowsAsync<MarketDataException>(() => app.CollectAsync(Timeframe.OneHour, app.LastClose("1h")));

        Assert.Equal(MarketDataFailure.Throttled, failure.Failure);
        Assert.InRange(failure.RetryAfter!.Value.TotalSeconds, 6, 7);
    }

    [Fact]
    public async Task A_symbol_Binance_does_not_know_is_reported_as_such()
    {
        await using var app = new MarketDataApp(postgres);
        app.Binance.On(HttpMethod.Get, "/api/v3/klines", _ => FakeBinance.Error(HttpStatusCode.BadRequest, -1121, "Invalid symbol."));

        var failure = await Assert.ThrowsAsync<MarketDataException>(() => app.CollectAsync(Timeframe.OneHour, app.LastClose("1h")));

        Assert.Equal(MarketDataFailure.UnknownSymbol, failure.Failure);
    }

    [Fact]
    public async Task A_failed_fetch_is_tried_again_by_the_next_caller()
    {
        await using var app = new MarketDataApp(postgres);
        var close = app.LastClose("1h");
        var failed = 0;
        app.Binance.On(HttpMethod.Get, "/api/v3/klines", request =>
            request.Parameter("interval") == "1h" && Interlocked.Increment(ref failed) == 1
                ? FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error; unable to process your request.")
                : FakeBinance.Json(BinanceSamples.Klines(request, app.Clock.GetUtcNow())));

        var failure = await Assert.ThrowsAsync<MarketDataException>(() => app.CollectAsync(Timeframe.OneHour, close));
        Assert.Equal(MarketDataFailure.Unavailable, failure.Failure);

        var snapshot = await app.CollectAsync(Timeframe.OneHour, close);

        Assert.Equal(CandleSeries.Window, snapshot.Candles.Candles.Count);
        Assert.Equal(2, app.Klines("1h").Count);
        Assert.Single(app.Klines("1d"));
    }

    [Fact]
    public async Task A_candle_that_has_not_closed_or_a_malformed_symbol_is_refused_before_anything_is_sent()
    {
        await using var app = new MarketDataApp(postgres);
        var next = app.LastClose("1h") + TimeSpan.FromHours(1);

        await Assert.ThrowsAsync<ArgumentException>(() => app.CollectAsync(Timeframe.OneHour, next));
        await Assert.ThrowsAsync<ArgumentException>(() => app.MarketData.CollectAsync(
            new MarketDataRequest(Market.Spot, TradingEnvironment.Live, "btcusdt", Timeframe.OneHour, app.LastClose("1h")),
            MarketDataApp.Cancellation));

        Assert.Empty(app.Binance.Requests);
    }
}
