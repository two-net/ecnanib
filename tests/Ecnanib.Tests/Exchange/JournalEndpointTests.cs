using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// What the trade journal (FR-EXE-07) reads from Binance: an account's trades in a window, with which side each was on,
/// Futures funding paid and received, the funding rates, and one-minute candles.
/// </summary>
public sealed class JournalEndpointTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset From = DateTimeOffset.FromUnixTimeMilliseconds(1570600000000);

    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Fact]
    public async Task An_accounts_trades_in_a_window_are_read_with_their_side_and_without_an_order()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        exchange.Binance.On(HttpMethod.Get, "/api/v3/myTrades", _ => FakeBinance.Json(BinanceSamples.SpotTrades));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/userTrades", _ => FakeBinance.Json(BinanceSamples.FuturesTrades));
        var (spotFrom, futuresFrom) = (DateTimeOffset.FromUnixTimeMilliseconds(1499865549000), DateTimeOffset.FromUnixTimeMilliseconds(1569514978000));

        var spot = await exchange.Account.GetTradesAsync(Market.Spot, key, "BTCUSDT", spotFrom, spotFrom.AddHours(24), RequestPriority.Normal, Cancellation);
        var futures = await exchange.Account.GetTradesAsync(Market.Futures, key, "BTCUSDT", futuresFrom, futuresFrom.AddDays(7), RequestPriority.Normal, Cancellation);

        Assert.Equal([(28457L, true), (28458L, true)], spot.Select(trade => (trade.TradeId, trade.Buyer)));
        Assert.Equal((698759L, false), (futures[0].TradeId, futures[0].Buyer));

        var request = exchange.Binance.To("/api/v3/myTrades").Single();
        Assert.Equal(
            [("symbol", "BTCUSDT"), ("startTime", "1499865549000"), ("endTime", "1499951949000"), ("limit", "1000")],
            request.Parameters.Where(parameter => parameter.Name is not ("timestamp" or "recvWindow" or "signature")));
        Assert.Null(exchange.Binance.To("/fapi/v1/userTrades").Single().Parameter("orderId"));
    }

    [Fact]
    public async Task A_window_longer_than_Binance_allows_is_refused_before_sending()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            exchange.Account.GetTradesAsync(Market.Spot, key, "BTCUSDT", From, From.AddHours(24).AddMilliseconds(1), RequestPriority.Normal, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            exchange.Account.GetTradesAsync(Market.Futures, key, "BTCUSDT", From, From.AddDays(7).AddMilliseconds(1), RequestPriority.Normal, Cancellation));
        Assert.Empty(exchange.Binance.Requests);
    }

    [Fact]
    public async Task Funding_fees_are_read_from_the_income_history_leaving_other_income_out()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/income", _ => FakeBinance.Json(BinanceSamples.FundingIncome));

        var fees = await exchange.Account.GetFundingFeesAsync(key, "BTCUSDT", From, From.AddDays(1), Cancellation);

        Assert.Equal(
            [
                new ExchangeFundingFee("BTCUSDT", 9689322392, DateTimeOffset.FromUnixTimeMilliseconds(1570608000000), -0.375m, "USDT"),
                new ExchangeFundingFee("BTCUSDT", 9689322394, DateTimeOffset.FromUnixTimeMilliseconds(1570636800000), 0.125m, "USDT"),
            ],
            fees);
        var request = exchange.Binance.To("/fapi/v1/income").Single();
        Assert.Equal(("FUNDING_FEE", "1000"), (request.Parameter("incomeType"), request.Parameter("limit")));
        Assert.NotNull(request.ApiKey);
    }

    [Fact]
    public async Task A_full_page_of_income_is_followed_by_the_next_from_its_last_time()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var key = ExchangeHarness.HmacKey();
        var page = string.Join(",", Enumerable.Range(0, 1000).Select(index =>
            $$"""{"symbol":"BTCUSDT","incomeType":"FUNDING_FEE","income":"-0.1","asset":"USDT","time":{{1570608000000 + index}},"tranId":{{index}}}"""));
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/fapi/v1/income",
            _ => FakeBinance.Json($"[{page}]"),
            _ => FakeBinance.Json("""[{"symbol":"BTCUSDT","incomeType":"FUNDING_FEE","income":"-0.1","asset":"USDT","time":1570608000999,"tranId":999},{"symbol":"BTCUSDT","incomeType":"FUNDING_FEE","income":"-0.2","asset":"USDT","time":1570608001500,"tranId":1000}]"""));

        var fees = await exchange.Account.GetFundingFeesAsync(key, "BTCUSDT", From, From.AddDays(1), Cancellation);

        Assert.Equal(1001, fees.Count);
        Assert.Equal("1570608000999", exchange.Binance.To("/fapi/v1/income")[1].Parameter("startTime"));
    }

    [Fact]
    public async Task Funding_rates_and_one_minute_candles_are_public()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/fundingRate", _ => FakeBinance.Json(BinanceSamples.FundingRates));
        exchange.Binance.On(HttpMethod.Get, "/api/v3/klines", _ => FakeBinance.Json("[]"));

        var rates = await exchange.MarketData.GetFundingRatesAsync(TradingEnvironment.Demo, "BTCUSDT", From, From.AddDays(1), Cancellation);
        await exchange.MarketData.GetCandlesAsync(Market.Spot, TradingEnvironment.Demo, "BNBUSDT", CandleInterval.OneMinute, From, From, 1, Cancellation);

        Assert.Equal([-0.0375m, 0.0001m], rates.Select(rate => rate.Rate));
        Assert.Null(exchange.Binance.To("/fapi/v1/fundingRate").Single().ApiKey);
        Assert.Equal("1m", exchange.Binance.To("/api/v3/klines").Single().Parameter("interval"));
    }
}
