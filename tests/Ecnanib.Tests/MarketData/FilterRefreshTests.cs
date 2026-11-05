using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.MarketData;

/// <summary>
/// FR-RISK-04: orders satisfy the symbol's filters, including its price limits, using exchange information
/// refreshed daily and after any filter-related rejection.
/// </summary>
public sealed class FilterRefreshTests(PostgresFixture postgres)
{
    private const string SpotPath = "/api/v3/exchangeInfo";

    private static CancellationToken Cancellation => MarketDataApp.Cancellation;

    [Fact]
    public async Task Percent_price_bands_are_read_and_stored()
    {
        string connectionString;

        await using (var first = new MarketDataApp(postgres))
        {
            first.Binance.On(HttpMethod.Get, SpotPath, _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()));
            first.Binance.On(HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(BinanceSamples.FuturesExchangeInfo));

            var spot = await first.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);
            var futures = await first.Symbols.FindAsync(Market.Futures, TradingEnvironment.Live, "BTCUSDT", Cancellation);

            Assert.Equal(new PercentPriceFilter(5m, 0.2m, 5m, 0.2m, 5), spot!.PercentPrice);
            Assert.Equal(new PercentPriceFilter(1.05m, 0m, 0m, 0.95m, null), futures!.PercentPrice);
            Assert.Null((await first.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "ETHBTC", Cancellation))!.PercentPrice);
            connectionString = first.ConnectionString;
        }

        // The stored copy answers after a restart while Binance is down, with the same bands.
        await using var second = new MarketDataApp(postgres, connectionString: connectionString);
        second.Binance.On(HttpMethod.Get, SpotPath, _ => FakeBinance.Error(System.Net.HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));

        var stored = await second.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);

        Assert.Equal(new PercentPriceFilter(5m, 0.2m, 5m, 0.2m, 5), stored!.PercentPrice);
    }

    [Fact]
    public async Task Spots_older_percent_price_filter_bounds_both_sides()
    {
        await using var app = new MarketDataApp(postgres);
        app.Binance.On(HttpMethod.Get, SpotPath, _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo(legacyPercentPrice: true)));

        var symbol = await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);

        Assert.Equal(new PercentPriceFilter(1.3m, 0.7m, 1.3m, 0.7m, 5), symbol!.PercentPrice);
    }

    [Fact]
    public async Task A_refresh_after_a_rejection_fetches_and_stores_the_filters_again()
    {
        await using var app = new MarketDataApp(postgres);
        app.Binance.OnSequence(
            HttpMethod.Get,
            SpotPath,
            _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()),
            _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo(btcTickSize: "0.10000000")));

        var before = await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        await app.Symbols.RefreshAsync(Market.Spot, TradingEnvironment.Live, Cancellation);
        var after = await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);

        Assert.Equal((0.01m, 0.1m), (before!.Price!.TickSize, after!.Price!.TickSize));
        Assert.Equal(2, app.Binance.To(SpotPath).Count);
        Assert.Equal(0.1m, await Sql.ScalarAsync<decimal>(app.ConnectionString, "SELECT tick_size FROM exchange_symbols WHERE symbol = 'BTCUSDT'", Cancellation));
    }

    [Fact]
    public async Task A_copy_younger_than_a_minute_is_kept_so_rejections_cannot_spend_the_weight_again_and_again()
    {
        await using var app = new MarketDataApp(postgres);
        app.Binance.On(HttpMethod.Get, SpotPath, _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()));

        // Nothing fetched yet, so there's nothing to refresh, and the next read fetches anyway.
        await app.Symbols.RefreshAsync(Market.Spot, TradingEnvironment.Live, Cancellation);
        Assert.Empty(app.Binance.To(SpotPath));

        await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);
        app.Clock.Advance(TimeSpan.FromSeconds(59));

        for (var rejection = 0; rejection < 5; rejection++)
        {
            await app.Symbols.RefreshAsync(Market.Spot, TradingEnvironment.Live, Cancellation);
        }

        Assert.Single(app.Binance.To(SpotPath));

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        await app.Symbols.RefreshAsync(Market.Spot, TradingEnvironment.Live, Cancellation);
        await app.Symbols.RefreshAsync(Market.Spot, TradingEnvironment.Live, Cancellation);

        Assert.Equal(2, app.Binance.To(SpotPath).Count);
    }

    [Fact]
    public async Task A_refresh_that_fails_keeps_the_copy_there_is()
    {
        await using var app = new MarketDataApp(postgres);
        app.Binance.OnSequence(
            HttpMethod.Get,
            SpotPath,
            _ => FakeBinance.Json(BinanceSamples.SpotExchangeInfo()),
            _ => FakeBinance.Error(System.Net.HttpStatusCode.ServiceUnavailable, -1001, "Internal error."));

        await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await app.Symbols.RefreshAsync(Market.Spot, TradingEnvironment.Live, Cancellation);

        var symbol = await app.Symbols.FindAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);

        Assert.Equal(0.01m, symbol!.Price!.TickSize);
    }

    [Theory]
    [InlineData(ExchangeFailure.Rejected, -1013, true)]
    [InlineData(ExchangeFailure.Rejected, -1111, true)]
    [InlineData(ExchangeFailure.Rejected, -4014, true)]
    [InlineData(ExchangeFailure.Rejected, -4023, true)]
    [InlineData(ExchangeFailure.Rejected, -4164, true)]
    [InlineData(ExchangeFailure.Rejected, -4024, true)]
    [InlineData(ExchangeFailure.Rejected, -2010, false)]
    [InlineData(ExchangeFailure.Rejected, -2019, false)]
    [InlineData(ExchangeFailure.Unknown, -1013, false)]
    [InlineData(ExchangeFailure.Throttled, -4164, false)]
    public void A_rejection_on_a_filter_is_recognised(ExchangeFailure failure, int code, bool filter)
    {
        var exception = new ExchangeException(ExchangeServer.FuturesDemo, failure, "Binance said no", code);

        Assert.Equal(filter, exception.IsFilterRejection);
    }

    [Fact]
    public void A_rejection_without_a_code_is_not_a_filter_rejection()
    {
        Assert.False(new ExchangeException(ExchangeServer.SpotDemo, ExchangeFailure.Rejected, "Binance said no").IsFilterRejection);
    }
}
