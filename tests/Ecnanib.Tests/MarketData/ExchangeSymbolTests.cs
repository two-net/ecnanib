using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.MarketData;

/// <summary>EXT-BIN-06 and SRS 6.1's ExchangeSymbol: Binance's symbols, kept for when it can't be reached.</summary>
public sealed class ExchangeSymbolTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => MarketDataApp.Cancellation;

    [Fact]
    public async Task Symbols_and_their_filters_are_stored_from_exchange_information()
    {
        await using var app = new MarketDataApp(postgres);
        app.Binance.On(HttpMethod.Get, "/api/v3/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()));

        var symbol = await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Demo, "BTCUSDT", Cancellation);

        Assert.NotNull(symbol);
        Assert.Equal(
            (true, "BTC", "USDT", new PriceFilter(0.01m, 1000000m, 0.01m), new QuantityFilter(0.00001m, 9000m, 0.00001m), new NotionalFilter(5m, 9000000m)),
            (symbol.IsTrading, symbol.BaseAsset, symbol.QuoteAsset, symbol.Price, symbol.Quantity, symbol.Notional));
        Assert.Contains("STOP_LOSS", symbol.OrderTypes);
        Assert.Null(await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Demo, "SOLUSDT", Cancellation));
        Assert.Null(await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Demo, "not a symbol", Cancellation));
        Assert.Equal(["BTCUSDT", "ETHBTC"], (await app.Symbols.ListAsync(Market.Spot, TradingEnvironment.Demo, Cancellation)).Select(listed => listed.Symbol));

        Assert.Equal(2L, await StoredAsync(app));
        Assert.Single(app.Binance.To("/api/v3/exchangeInfo"));
    }

    [Fact]
    public async Task A_refresh_replaces_the_stored_copy_and_drops_what_Binance_no_longer_lists()
    {
        await using var app = new MarketDataApp(postgres, new() { ["Binance:ExchangeInfoRefreshHours"] = "6" });
        app.Binance.OnSequence(
            HttpMethod.Get,
            "/api/v3/exchangeInfo",
            _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()),
            _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo(btcTickSize: "0.10000000", ethBtc: false)));

        var before = await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);
        app.Clock.Advance(TimeSpan.FromHours(6));
        var after = await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);

        Assert.Equal((0.01m, 0.1m), (before!.Price!.TickSize, after!.Price!.TickSize));
        Assert.True(after.RefreshedAt > before.RefreshedAt);
        Assert.Equal(1L, await StoredAsync(app));
        Assert.Equal(0.1m, await Sql.ScalarAsync<decimal>(app.ConnectionString, "SELECT tick_size FROM exchange_symbols WHERE symbol = 'BTCUSDT'", Cancellation));
    }

    [Fact]
    public async Task When_Binance_is_down_after_a_restart_the_stored_copy_answers()
    {
        string connectionString;
        DateTimeOffset refreshedAt;

        await using (var first = new MarketDataApp(postgres))
        {
            first.Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.FuturesExchangeInfo));
            refreshedAt = (await first.Symbols.FindAsync(Market.Futures, TradingEnvironment.Live, "BTCUSDT", Cancellation))!.RefreshedAt;
            connectionString = first.ConnectionString;
        }

        await using var second = new MarketDataApp(postgres, connectionString: connectionString);
        second.Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Error(HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));

        var stored = await second.Symbols.FindAsync(Market.Futures, TradingEnvironment.Live, "BTCUSDT", Cancellation);

        Assert.Equal(
            ("PERPETUAL", new PriceFilter(261.1m, 809484m, 0.1m), new NotionalFilter(50m, null)),
            (stored!.ContractType, stored.Price, stored.Notional));

        // PostgreSQL keeps microseconds, and .NET ticks are a tenth of that.
        Assert.InRange((stored.RefreshedAt - refreshedAt).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.Single(await second.Symbols.ListAsync(Market.Futures, TradingEnvironment.Live, Cancellation));
        Assert.Null(await second.Symbols.FindAsync(Market.Futures, TradingEnvironment.Live, "ETHUSDT", Cancellation));

        // Nothing was stored for Demo, so there's nothing to answer with.
        var failure = await Assert.ThrowsAsync<MarketDataException>(
            () => second.Symbols.FindAsync(Market.Futures, TradingEnvironment.Demo, "BTCUSDT", Cancellation));
        Assert.Equal(MarketDataFailure.Unavailable, failure.Failure);
    }

    private static Task<long> StoredAsync(MarketDataApp app) =>
        Sql.ScalarAsync<long>(app.ConnectionString, "SELECT count(*) FROM exchange_symbols", Cancellation);
}
