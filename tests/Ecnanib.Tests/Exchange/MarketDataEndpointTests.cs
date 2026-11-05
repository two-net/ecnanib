using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>EXT-BIN-02, EXT-BIN-04, EXT-BIN-08: candles, quotes, funding and positions, from the right server, within the budget.</summary>
public sealed class MarketDataEndpointTests(PostgresFixture postgres)
{
    private const string TwoCandles = """
        [[1792724400000,"81974.00000000","82033.84000000","81712.00000000","81889.32000000","351.29666000",1792727999999,"28770374.58921650",94139,"152.20836000","12465780.27522830","0"],
         [1792728000000,"81889.32000000","81889.33000000","81542.01000000","81587.32000000","290.30461000",1792731599999,"23723806.82285500",64844,"120.38563000","9838562.61604330","0"]]
        """;

    private static readonly DateTimeOffset First = DateTimeOffset.FromUnixTimeMilliseconds(1792724400000);

    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Fact]
    public async Task Candles_are_read_as_decimals_with_the_range_and_limit_Binance_was_asked_for()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/klines", _ => FakeBinance.Json(TwoCandles));

        var candles = await exchange.MarketData.GetCandlesAsync(
            Market.Spot, TradingEnvironment.Live, "BTCUSDT", CandleInterval.OneHour, First, First.AddHours(1), 2, Cancellation);

        Assert.Equal(
            new ExchangeCandle(First, 81974m, 82033.84m, 81712m, 81889.32m, 351.29666m, First.AddHours(1).AddMilliseconds(-1), 28770374.5892165m, 94139),
            candles[0]);
        Assert.Equal((2, 81587.32m), (candles.Count, candles[1].Close));

        var request = Assert.Single(exchange.Binance.Requests);
        Assert.Equal(FakeBinance.MarketData, request.Host);
        Assert.Equal(
            [("symbol", "BTCUSDT"), ("interval", "1h"), ("startTime", "1792724400000"), ("endTime", "1792728000000"), ("limit", "2")],
            request.Parameters);
        Assert.Null(request.ApiKey);
    }

    [Theory]
    [InlineData(Market.Spot, TradingEnvironment.Demo, CandleInterval.FifteenMinutes, FakeBinance.SpotDemo, "/api/v3/klines", "15m")]
    [InlineData(Market.Futures, TradingEnvironment.Live, CandleInterval.FourHours, FakeBinance.FuturesLive, "/fapi/v1/klines", "4h")]
    [InlineData(Market.Futures, TradingEnvironment.Demo, CandleInterval.OneDay, FakeBinance.FuturesDemo, "/fapi/v1/klines", "1d")]
    [InlineData(Market.Spot, TradingEnvironment.Live, CandleInterval.OneWeek, FakeBinance.MarketData, "/api/v3/klines", "1w")]
    public async Task Each_market_and_environment_has_its_own_server_and_Demo_data_never_comes_from_Live(
        Market market,
        TradingEnvironment environment,
        CandleInterval interval,
        string host,
        string path,
        string code)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, path, _ => FakeBinance.Json("[]"));

        Assert.Empty(await exchange.MarketData.GetCandlesAsync(market, environment, "BTCUSDT", interval, from: null, First, 10, Cancellation));

        var request = Assert.Single(exchange.Binance.Requests);
        Assert.Equal((host, path, code, null), (request.Host, request.Path, request.Parameter("interval"), request.Parameter("startTime")));
    }

    [Theory]
    [InlineData(Market.Futures, 99, 1)]
    [InlineData(Market.Futures, 100, 2)]
    [InlineData(Market.Futures, 500, 5)]
    [InlineData(Market.Futures, 1000, 5)]
    [InlineData(Market.Spot, 1000, 2)]
    public async Task Candles_reserve_the_weight_Binance_charges_for_their_limit(Market market, int limit, int weight)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.StartOfMinute();
        var arrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var path = market == Market.Futures ? "/fapi/v1/klines" : "/api/v3/klines";
        exchange.Binance.On(HttpMethod.Get, path, async (_, _) =>
        {
            arrived.SetResult();
            await release.Task;

            return FakeBinance.Json("[]", headers: ("X-MBX-USED-WEIGHT-1M", weight.ToString(CultureInfo.InvariantCulture)));
        });

        var call = exchange.MarketData.GetCandlesAsync(market, TradingEnvironment.Demo, "BTCUSDT", CandleInterval.OneHour, null, First, limit, Cancellation);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);

        Assert.Equal(weight, exchange.Usage(ExchangeServers.Public(market, TradingEnvironment.Demo)).UsedWeight);

        release.SetResult();
        await call;
    }

    [Fact]
    public async Task Candle_requests_Binance_would_refuse_are_not_sent()
    {
        await using var exchange = new ExchangeHarness(postgres);
        var data = exchange.MarketData;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => data.GetCandlesAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", CandleInterval.OneHour, null, First, 0, Cancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => data.GetCandlesAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", CandleInterval.OneHour, null, First, 1001, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => data.GetCandlesAsync(Market.Spot, TradingEnvironment.Live, "btc/usdt", CandleInterval.OneHour, null, First, 10, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => data.GetCandlesAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", CandleInterval.OneHour, First.AddHours(1), First, 10, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => data.GetQuoteAsync(Market.Spot, TradingEnvironment.Live, "", Cancellation));

        Assert.Empty(exchange.Binance.Requests);
    }

    [Fact]
    public async Task A_body_this_version_cannot_read_is_a_rejection()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/klines", _ => FakeBinance.Json("""{"candles":[]}"""));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/klines", _ => FakeBinance.Json("""[[1792724400000,"one","2","3","4","5",1792727999999,"6",7]]"""));

        var spot = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.MarketData.GetCandlesAsync(Market.Spot, TradingEnvironment.Demo, "BTCUSDT", CandleInterval.OneHour, null, First, 10, Cancellation));
        var futures = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.MarketData.GetCandlesAsync(Market.Futures, TradingEnvironment.Demo, "BTCUSDT", CandleInterval.OneHour, null, First, 10, Cancellation));

        Assert.Equal((ExchangeFailure.Rejected, ExchangeFailure.Rejected), (spot.Failure, futures.Failure));
        Assert.DoesNotContain("?", spot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Spot_reads_its_price_and_book_from_one_ticker_and_Futures_from_two()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/api/v3/ticker/24hr", _ => FakeBinance.Json(BinanceSamples.SpotTicker24h));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/ticker/bookTicker", _ => FakeBinance.Json(BinanceSamples.FuturesBookTicker));
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/ticker/price", _ => FakeBinance.Json(BinanceSamples.FuturesPrice));

        var spot = await exchange.MarketData.GetQuoteAsync(Market.Spot, TradingEnvironment.Live, "BTCUSDT", Cancellation);
        var futures = await exchange.MarketData.GetQuoteAsync(Market.Futures, TradingEnvironment.Demo, "BTCUSDT", Cancellation);

        Assert.Equal(new ExchangeQuote("BTCUSDT", 60123.45m, 60123.44m, 1.25m, 60123.46m, 0.75m, spot.FetchedAt), spot);
        Assert.Equal(new ExchangeQuote("BTCUSDT", 60120.10m, 60120.10m, 3.512m, 60120.20m, 0.041m, futures.FetchedAt), futures);
        Assert.Equal(
            [(FakeBinance.FuturesDemo, "/fapi/v1/ticker/bookTicker"), (FakeBinance.FuturesDemo, "/fapi/v1/ticker/price"), (FakeBinance.MarketData, "/api/v3/ticker/24hr")],
            exchange.Binance.Requests.Select(request => (request.Host, request.Path)).Order());
        Assert.All(exchange.Binance.Requests, request => Assert.Equal([("symbol", "BTCUSDT")], request.Parameters));
    }

    [Fact]
    public async Task Funding_is_read_from_the_premium_index_and_a_contract_without_it_has_none()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/fapi/v1/premiumIndex",
            _ => FakeBinance.Json(BinanceSamples.PremiumIndex()),
            _ => FakeBinance.Json(BinanceSamples.PremiumIndex(funding: false)));

        var perpetual = await exchange.MarketData.GetPremiumIndexAsync(TradingEnvironment.Live, "BTCUSDT", Cancellation);
        var delivery = await exchange.MarketData.GetPremiumIndexAsync(TradingEnvironment.Live, "BTCUSDT", Cancellation);

        Assert.Equal(
            new ExchangePremiumIndex("BTCUSDT", 60118.75m, 60131.0234m, 0.0001m, DateTimeOffset.FromUnixTimeMilliseconds(1793808000000), perpetual.FetchedAt),
            perpetual);
        Assert.Equal(((decimal?)null, (DateTimeOffset?)null), (delivery.FundingRate, delivery.NextFundingTime));
        Assert.All(exchange.Binance.Requests, request => Assert.Equal(FakeBinance.FuturesLive, request.Host));
    }

    [Fact]
    public async Task A_position_is_read_signed_for_one_symbol_and_an_empty_one_is_left_out()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/fapi/v3/positionRisk",
            _ => FakeBinance.Json(BinanceSamples.PositionRisk),
            _ => FakeBinance.Json(BinanceSamples.PositionRisk.Replace("\"liquidationPrice\":\"66012.3\"", "\"liquidationPrice\":\"0\"", StringComparison.Ordinal)));
        var key = ExchangeHarness.HmacKey(TradingEnvironment.Demo);

        var positions = await exchange.Account.GetPositionAsync(key, "BTCUSDT", RequestPriority.Protective, Cancellation);

        Assert.Equal(
            [
                new FuturesPosition(
                    "BTCUSDT", "BOTH", -0.010m, 60500.0m, 60530.25m, 60123.4m, 66012.3m, 3.766m, "USDT", DateTimeOffset.FromUnixTimeMilliseconds(1720736417660)),
            ],
            positions);

        var request = Assert.Single(exchange.Binance.To("/fapi/v3/positionRisk"));
        Assert.Equal((FakeBinance.FuturesDemo, ExchangeHarness.ExampleApiKey, "BTCUSDT"), (request.Host, request.ApiKey, request.Parameter("symbol")));
        Assert.NotNull(request.Parameter("signature"));

        // Binance sends 0 when nothing would liquidate the position.
        Assert.Null(Assert.Single(await exchange.Account.GetPositionAsync(key, "BTCUSDT", RequestPriority.Normal, Cancellation)).LiquidationPrice);

        await Assert.ThrowsAsync<ArgumentException>(() => exchange.Account.GetPositionAsync(key, "BTC USDT", RequestPriority.Normal, Cancellation));
        Assert.Equal(2, exchange.Binance.To("/fapi/v3/positionRisk").Count);
    }
}
