using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>EXT-BIN-06.</summary>
public sealed class ExchangeInfoTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Fact]
    public async Task Filters_are_read_as_decimals()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.FuturesExchangeInfo));

        var spot = await exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Demo, Cancellation);
        var futures = await exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Demo, Cancellation);

        Assert.Equal(
            new SymbolRules(
                "BTCUSDT",
                "TRADING",
                "BTC",
                "USDT",
                null,
                spot.Find("BTCUSDT")!.OrderTypes,
                new PriceRules(0.01m, 1000000m, 0.01m),
                new QuantityRules(0.00001m, 9000m, 0.00001m),
                new QuantityRules(0m, 127.44699891m, 0m),
                new NotionalRules(5m, 9000000m)),
            spot.Find("BTCUSDT"));
        Assert.Contains("STOP_LOSS", spot.Find("BTCUSDT")!.OrderTypes);
        Assert.False(spot.Find("ETHBTC")!.IsTrading);
        Assert.Null(spot.Find("ETHBTC")!.Price);

        var perpetual = futures.Find("BTCUSDT")!;
        Assert.Equal(
            ("PERPETUAL", new PriceRules(261.1m, 809484m, 0.1m), new QuantityRules(0.0001m, 1000m, 0.0001m), new NotionalRules(50m, null)),
            (perpetual.ContractType, perpetual.Price, perpetual.Quantity, perpetual.Notional));
        Assert.Null(futures.Find("ETHUSDT"));
    }

    [Fact]
    public async Task It_is_fetched_once_and_again_when_it_is_older_than_the_setting()
    {
        await using var exchange = new ExchangeHarness(postgres, new() { ["Binance:ExchangeInfoRefreshHours"] = "6" });
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.FuturesExchangeInfo));

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Live, Cancellation)));
        exchange.Clock.Advance(TimeSpan.FromHours(5));
        await exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Live, Cancellation);
        Assert.Single(exchange.Binance.To("/fapi/v1/exchangeInfo"));

        exchange.Clock.Advance(TimeSpan.FromHours(1));
        var refreshed = await exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Live, Cancellation);
        Assert.Equal(2, exchange.Binance.To("/fapi/v1/exchangeInfo").Count);
        Assert.InRange((exchange.Clock.GetUtcNow() - refreshed.RefreshedAt).TotalSeconds, 0, 5);
    }

    [Fact]
    public async Task Each_market_and_environment_has_its_own_copy()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.FuturesExchangeInfo));

        var live = await exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Live, Cancellation);
        var demo = await exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Demo, Cancellation);

        Assert.Equal((TradingEnvironment.Live, TradingEnvironment.Demo), (live.Environment, demo.Environment));
        Assert.Equal([FakeBinance.FuturesLive, FakeBinance.FuturesDemo], exchange.Binance.To("/fapi/v1/exchangeInfo").Select(request => request.Host));
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_copy_and_waits_a_minute_before_trying_again()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/api/v3/exchangeInfo",
            _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()),
            _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable),
            _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()));

        var first = await exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Demo, Cancellation);
        exchange.Clock.Advance(TimeSpan.FromHours(25));

        Assert.Same(first, await exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Demo, Cancellation));
        Assert.Same(first, await exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Demo, Cancellation));
        Assert.Equal(2, exchange.Binance.To("/api/v3/exchangeInfo").Count);

        exchange.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotSame(first, await exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Demo, Cancellation));
        Assert.Equal(3, exchange.Binance.To("/api/v3/exchangeInfo").Count);
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_for_a_refresh_gets_the_last_copy_and_the_refresh_carries_on()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var sent = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.FuturesExchangeInfo));
        var first = await exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Demo, Cancellation);

        exchange.Clock.Advance(TimeSpan.FromHours(25));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", async (_, _) =>
        {
            sent.SetResult();
            await release.Task;
            return FakeBinance.Json(BinanceSamples.FuturesExchangeInfo);
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var waiting = exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Demo, caller.Token);
        await sent.Task.WaitAsync(Cancellation);
        await caller.CancelAsync();

        Assert.Same(first, await waiting);

        release.SetResult();
        await LoggedApp.UntilAsync(async () =>
            !ReferenceEquals(first, await exchange.MarketData.GetExchangeInfoAsync(Market.Futures, TradingEnvironment.Demo, Cancellation)));
        Assert.Equal(2, exchange.Binance.To("/fapi/v1/exchangeInfo").Count);
    }

    [Fact]
    public async Task With_no_copy_a_failure_is_thrown()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/exchangeInfo", _ => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.MarketData.GetExchangeInfoAsync(Market.Spot, TradingEnvironment.Live, Cancellation));

        Assert.Equal(ExchangeServer.SpotMarketData, exception.Server);
    }
}
