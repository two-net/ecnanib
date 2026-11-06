using System.Globalization;
using System.Net;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Exchange;

/// <summary>
/// The Futures account settings behind EXT-BIN-07 and FR-RISK-09, the leverage brackets and the funding intervals
/// behind the Risk Engine's Futures inputs, and how their answers are read.
/// </summary>
public sealed class FuturesAccountTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => ExchangeHarness.Cancellation;

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task The_account_config_says_whether_it_may_trade_and_its_modes(bool canTrade, bool hedgeMode, bool multiAssets)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/accountConfig", _ => FakeBinance.Json(BinanceSamples.FuturesAccountConfig(canTrade, hedgeMode, multiAssets)));

        var config = await exchange.Account.GetFuturesAccountConfigAsync(ExchangeHarness.HmacKey(), Cancellation);

        Assert.Equal(new FuturesAccountConfig(canTrade, hedgeMode, multiAssets), config);
        var request = Assert.Single(exchange.Binance.To("/fapi/v1/accountConfig"));
        Assert.Equal((FakeBinance.FuturesDemo, ExchangeHarness.ExampleApiKey), (request.Host, request.ApiKey));
        Assert.NotNull(request.Parameter("signature"));
    }

    [Theory]
    [InlineData("ISOLATED", FuturesMarginType.Isolated)]
    [InlineData("CROSSED", FuturesMarginType.Crossed)]
    [InlineData("SOMETHING_NEW", null)]
    public async Task A_symbols_config_gives_its_margin_type_and_leverage(string marginType, FuturesMarginType? expected)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/symbolConfig", _ => FakeBinance.Json(BinanceSamples.SymbolConfig(marginType, leverage: 7)));

        var config = await exchange.Account.GetFuturesSymbolConfigAsync(ExchangeHarness.HmacKey(TradingEnvironment.Live), "BTCUSDT", Cancellation);

        Assert.Equal(new FuturesSymbolConfig("BTCUSDT", expected, 7), config);
        var request = Assert.Single(exchange.Binance.To("/fapi/v1/symbolConfig"));
        Assert.Equal((FakeBinance.FuturesLive, "BTCUSDT"), (request.Host, request.Parameter("symbol")));
    }

    [Fact]
    public async Task A_symbol_Binance_lists_nothing_for_has_no_config()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/symbolConfig", _ => FakeBinance.Json(BinanceSamples.SymbolConfig(symbol: "ETHUSDT")));

        Assert.Null(await exchange.Account.GetFuturesSymbolConfigAsync(ExchangeHarness.HmacKey(), "BTCUSDT", Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => exchange.Account.GetFuturesSymbolConfigAsync(ExchangeHarness.HmacKey(), "btc", Cancellation));
        Assert.Single(exchange.Binance.To("/fapi/v1/symbolConfig"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leverage_brackets_are_read_as_an_array_or_one_symbol_lowest_first(bool single)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/leverageBracket", _ => FakeBinance.Json(BinanceSamples.LeverageBrackets(single)));

        var brackets = await exchange.Account.GetLeverageBracketsAsync(ExchangeHarness.HmacKey(), "BTCUSDT", Cancellation);

        Assert.Equal(
            [
                new ExchangeLeverageBracket(1, 125, 0m, 500_000m, 0.004m, 0m),
                new ExchangeLeverageBracket(2, 100, 500_000m, 600_000m, 0.005m, 750m),
                new ExchangeLeverageBracket(3, 75, 600_000m, 3_000_000m, 0.0065m, 1650m),
            ],
            brackets);
        var request = Assert.Single(exchange.Binance.To("/fapi/v1/leverageBracket"));
        Assert.Equal(("BTCUSDT", ExchangeHarness.ExampleApiKey), (request.Parameter("symbol"), request.ApiKey));
    }

    [Fact]
    public async Task Brackets_for_another_symbol_are_none_and_an_unreadable_answer_is_a_rejection()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.OnSequence(
            HttpMethod.Get,
            "/fapi/v1/leverageBracket",
            _ => FakeBinance.Json(BinanceSamples.LeverageBrackets(symbol: "ETHUSDT")),
            _ => FakeBinance.Json("\"brackets\""));

        Assert.Empty(await exchange.Account.GetLeverageBracketsAsync(ExchangeHarness.HmacKey(), "BTCUSDT", Cancellation));

        var failure = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Account.GetLeverageBracketsAsync(ExchangeHarness.HmacKey(), "BTCUSDT", Cancellation));
        Assert.Equal(ExchangeFailure.Rejected, failure.Failure);
    }

    [Fact]
    public async Task Funding_intervals_list_only_the_symbols_Binance_adjusted_and_need_no_key()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Get, "/fapi/v1/fundingInfo", _ => FakeBinance.Json(BinanceSamples.FundingInfo));

        var intervals = await exchange.MarketData.GetFundingIntervalsAsync(TradingEnvironment.Live, Cancellation);

        // XRPUSDT is listed for its cap alone, with no interval, so it's left to the default like BTCUSDT.
        Assert.Equal(new Dictionary<string, int> { ["ETHUSDT"] = 4 }, intervals);
        var request = Assert.Single(exchange.Binance.To("/fapi/v1/fundingInfo"));
        Assert.Equal((FakeBinance.FuturesLive, null, null), (request.Host, request.ApiKey, request.Parameter("signature")));
    }

    [Fact]
    public async Task Setting_isolated_margin_and_leverage_sends_signed_POSTs_with_their_parameters()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/marginType", _ => FakeBinance.Json(BinanceSamples.MarginTypeChanged));
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/leverage", request =>
            FakeBinance.Json(BinanceSamples.Leverage(int.Parse(request.Parameter("leverage")!, CultureInfo.InvariantCulture))));
        var key = ExchangeHarness.HmacKey();

        await exchange.Trading.SetMarginTypeAsync(key, "BTCUSDT", FuturesMarginType.Isolated, Cancellation);
        var leverage = await exchange.Trading.SetLeverageAsync(key, "BTCUSDT", 3, Cancellation);

        Assert.Equal(3, leverage);
        var margin = Assert.Single(exchange.Binance.To("/fapi/v1/marginType"));
        var set = Assert.Single(exchange.Binance.To("/fapi/v1/leverage"));
        Assert.Equal((HttpMethod.Post, FakeBinance.FuturesDemo, ExchangeHarness.ExampleApiKey), (margin.Method, margin.Host, margin.ApiKey));
        Assert.Equal((HttpMethod.Post, FakeBinance.FuturesDemo, ExchangeHarness.ExampleApiKey), (set.Method, set.Host, set.ApiKey));
        Assert.Equal([("symbol", "BTCUSDT"), ("marginType", "ISOLATED")], Unsigned(margin));
        Assert.Equal([("symbol", "BTCUSDT"), ("leverage", "3")], Unsigned(set));
        Assert.NotNull(margin.Parameter("signature"));
        Assert.NotNull(set.Parameter("signature"));
    }

    [Fact]
    public async Task Binances_refusals_come_back_with_their_codes_and_bad_values_arent_sent()
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/marginType", _ =>
            FakeBinance.Error(HttpStatusCode.BadRequest, -4046, "No need to change margin type."));
        exchange.Binance.On(HttpMethod.Post, "/fapi/v1/leverage", _ =>
            FakeBinance.Error(HttpStatusCode.BadRequest, -4161, "Leverage reduction is not supported in Isolated Margin Mode with open positions"));
        var key = ExchangeHarness.HmacKey();

        var margin = await Assert.ThrowsAsync<ExchangeException>(
            () => exchange.Trading.SetMarginTypeAsync(key, "BTCUSDT", FuturesMarginType.Isolated, Cancellation));
        var leverage = await Assert.ThrowsAsync<ExchangeException>(() => exchange.Trading.SetLeverageAsync(key, "BTCUSDT", 2, Cancellation));

        Assert.Equal((ExchangeFailure.Rejected, -4046), (margin.Failure, margin.Code));
        Assert.Equal((ExchangeFailure.Rejected, -4161), (leverage.Failure, leverage.Code));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => exchange.Trading.SetLeverageAsync(key, "BTCUSDT", 0, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => exchange.Trading.SetMarginTypeAsync(key, "BTC-USDT", FuturesMarginType.Isolated, Cancellation));
        Assert.Single(exchange.Binance.To("/fapi/v1/marginType"));
        Assert.Single(exchange.Binance.To("/fapi/v1/leverage"));
    }

    [Theory]
    [InlineData("/fapi/v1/accountConfig", 5)]
    [InlineData("/fapi/v1/symbolConfig", 5)]
    [InlineData("/fapi/v1/leverageBracket", 1)]
    [InlineData("/fapi/v1/fundingInfo", 1)]
    public async Task Each_read_reserves_the_weight_Binance_charges(string path, int weight)
    {
        await using var exchange = new ExchangeHarness(postgres);
        exchange.StartOfMinute();
        var arrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        exchange.Binance.On(HttpMethod.Get, path, async (_, _) =>
        {
            arrived.SetResult();
            await release.Task;

            return FakeBinance.Json("[]");
        });

        var key = ExchangeHarness.HmacKey();
        Task call = path switch
        {
            "/fapi/v1/accountConfig" => exchange.Account.GetFuturesAccountConfigAsync(key, Cancellation),
            "/fapi/v1/symbolConfig" => exchange.Account.GetFuturesSymbolConfigAsync(key, "BTCUSDT", Cancellation),
            "/fapi/v1/leverageBracket" => exchange.Account.GetLeverageBracketsAsync(key, "BTCUSDT", Cancellation),
            _ => exchange.MarketData.GetFundingIntervalsAsync(TradingEnvironment.Demo, Cancellation),
        };
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);

        // A signed read syncs the clock first, whose answer reports a weight of 1.
        var synced = path == "/fapi/v1/fundingInfo" ? 0 : 1;
        Assert.Equal(synced + weight, exchange.Usage(ExchangeServer.FuturesDemo).UsedWeight);

        release.SetResult();
        await ((Task)call).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static IEnumerable<(string Name, string Value)> Unsigned(RecordedRequest request) =>
        request.Parameters.Where(parameter => parameter.Name is not ("timestamp" or "recvWindow" or "signature"));
}
