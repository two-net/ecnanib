using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// The gateway reads the market data of Binance: candles, prices, and the rules of the symbols,
/// and on the Futures market also the mark price, the funding rate, and the open interest
/// (FR-BIN-3). Each is read with a call of Binance's public API that needs no credential, at the
/// address of its market in its environment (FR-BIN-1, FR-BIN-2), and an answer reads in whole
/// or is a failure. Only closed candles are given (FR-CYC-1). That it is read once and shared is
/// in <see cref="MarketDataSharingTests"/>, and for how long it is held in
/// <see cref="CandleCacheTests"/>.
/// </summary>
public class MarketDataTests
{
    private const string Symbol = MarketService.Symbol;

    public static TheoryData<TradingMode> Environments => [TradingMode.Live, TradingMode.Demo];

    [Theory]
    [MemberData(nameof(BinanceCalls.MarketsAndEnvironments), MemberType = typeof(BinanceCalls))]
    public async Task The_candles_of_a_symbol_are_read_at_the_address_of_its_market_with_a_call_that_needs_no_credential(
        Market market, TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var candles = await data.GetCandlesAsync(
            market, mode, Symbol, CandleInterval.FifteenMinutes, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, candles.Outcome);
        Assert.Equal(BinanceCalls.ClosedAt(BinanceCalls.Now, CandleInterval.FifteenMinutes), candles.Candles);

        // It says when it was read, by the clock of the application.
        Assert.Equal(BinanceCalls.Now, candles.ReadAt);

        // One call, which reads, to the address of the market in the environment. It asks for
        // the symbol, the length of a candle, and as many rows as a read gives and one more.
        var call = Assert.Single(binance.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal($"{BinanceCalls.AddressOf(market, mode)}/{(market == Market.Spot ? "api/v3/klines" : "fapi/v1/klines")}", call.Endpoint);
        Assert.Equal([("symbol", Symbol), ("interval", "15m"), ("limit", "499")], call.Query);
        Assert.Equal(499, ExchangeCandles.Most + 1);

        // It is the service's own: no key, no signature, and no body.
        Assert.False(call.Headers.ContainsKey("X-MBX-APIKEY"));
        Assert.Null(call.Body);
    }

    [Fact]
    public async Task Each_length_of_a_candle_is_asked_for_by_the_name_that_binance_gives_it()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        // The four timeframes of an agent, and the week that is the context of the longer ones.
        Assert.Equal(Enum.GetValues<CandleInterval>(), BinanceCalls.Intervals.Select(known => known.Interval));

        foreach (var (interval, _, _) in BinanceCalls.Intervals)
        {
            await data.GetCandlesAsync(Market.Futures, TradingMode.Demo, Symbol, interval, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["15m", "1h", "4h", "1d", "1w"], binance.Calls.Select(call => call.Query[1].Value));
    }

    [Fact]
    public async Task Candles_are_read_to_the_digit_oldest_first_and_the_one_in_progress_is_not_given()
    {
        // Three rows of an hour, as Binance's documentation gives a row. The last began at
        // 09:00 and is in progress at 09:30, which is what the clock shows.
        const string answer =
            """
            [
              [ 1794726000000, "104102.34000000", "104390.00000000", "103977.01000000", "104250.50000000", "812.44213000",
                1794729599999, "84631234.12345678", 151234, "401.11000000", "41800000.00000000", "0" ],
              [ 1794729600000, "104250.50000000", "104611.11000000", "104200.00000000", "104588.99000000", "1023.00000001",
                1794733199999, "106800000.00000000", 180001, "512.00000000", "53400000.00000000", "0" ],
              [ 1794733200000, "104588.99000000", "104700.00000000", "104500.00000000", "104650.00000000", "310.50000000",
                1794736799999, "32400000.00000000", 60210, "150.00000000", "15600000.00000000", "0" ]
            ]
            """;
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var candles = await data.GetCandlesAsync(
            Market.Spot, TradingMode.Live, Symbol, CandleInterval.OneHour, TestContext.Current.CancellationToken);

        // A candle ends when the next one begins, which is the close that a cycle starts at.
        var day = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            [
                new ExchangeCandle(day.AddHours(7), day.AddHours(8), 104102.34m, 104390m, 103977.01m, 104250.5m, 812.44213m),
                new ExchangeCandle(day.AddHours(8), day.AddHours(9), 104250.5m, 104611.11m, 104200m, 104588.99m, 1023.00000001m),
            ],
            candles.Candles);
    }

    [Theory]
    [InlineData("[]")]

    // The one row is the candle in progress.
    [InlineData("""[ [ 1794733200000, "1", "2", "0.5", "1.5", "10", 1794736799999, "15", 3, "5", "7.5", "0" ] ]""")]
    public async Task A_symbol_without_a_closed_candle_has_none_and_that_is_no_failure(string answer)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var candles = await data.GetCandlesAsync(
            Market.Spot, TradingMode.Demo, Symbol, CandleInterval.OneHour, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, candles.Outcome);
        Assert.Empty(candles.Candles);
        Assert.Equal(BinanceCalls.Now, candles.ReadAt);
    }

    [Theory]

    // Not a list of rows, and a row that is not one.
    [InlineData("""{ "symbol": "BTCUSDT" }""")]
    [InlineData("""[ { "openTime": 1794729600000 } ]""")]
    [InlineData("""[ [ 1794729600000, "1", "2", "0.5", "1.5", "10" ] ]""")]

    // A time that is none, a price that is a number and not text, and one that is no number.
    [InlineData("""[ [ "1794729600000", "1", "2", "0.5", "1.5", "10", 1794733199999 ] ]""")]
    [InlineData("""[ [ 1794729600000, "1", "2", "0.5", "1.5", "10", 1e40 ] ]""")]
    [InlineData("""[ [ 1794729600000, 1, 2, 0.5, 1.5, 10, 1794733199999 ] ]""")]
    [InlineData("""[ [ 1794729600000, "1", "2", "low", "1.5", "10", 1794733199999 ] ]""")]
    [InlineData("""[ [ 1794729600000, "1", "2", "0.5", "1.5", "-10", 1794733199999 ] ]""")]

    // A candle that ends before it begins, and rows that are not in the order of their time.
    [InlineData("""[ [ 1794729600000, "1", "2", "0.5", "1.5", "10", 1794729599999 ] ]""")]
    [InlineData(
        """
        [ [ 1794729600000, "1", "2", "0.5", "1.5", "10", 1794733199999 ], [ 1794726000000, "1", "2", "0.5", "1.5", "10", 1794729599999 ],
          [ 1794733200000, "1", "2", "0.5", "1.5", "10", 1794736799999 ] ]
        """)]
    public async Task Candles_that_do_not_read_in_whole_are_a_failure_and_no_part_of_them_is_given(string answer)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var candles = await data.GetCandlesAsync(
            Market.Futures, TradingMode.Live, Symbol, CandleInterval.OneHour, TestContext.Current.CancellationToken);

        // A part of them would look like a gap in the market.
        Assert.Equal(ExchangeCallOutcome.Failed, candles.Outcome);
        Assert.Empty(candles.Candles);
        Assert.Null(candles.ReadAt);

        // The status says that the call went well, so the gateway says that it gave nothing.
        var said = Assert.Single(application.Events(), written => written.Area == BinanceCalls.GatewayArea);
        Assert.Equal("fapi/v1/klines", said.Text("Path"));
        Assert.Equal("NotExpected", said.Text("Reason"));
    }

    [Theory]
    [MemberData(nameof(BinanceCalls.MarketsAndEnvironments), MemberType = typeof(BinanceCalls))]
    public async Task The_price_of_a_symbol_is_read_at_the_address_of_its_market(Market market, TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        Assert.Equal(
            new ExchangePrice(ExchangeCallOutcome.Succeeded, market == Market.Spot ? 104250.01m : 104251.7m, BinanceCalls.Now),
            await data.GetPriceAsync(market, mode, Symbol, TestContext.Current.CancellationToken));

        var call = Assert.Single(binance.Calls);
        Assert.Equal(
            $"GET {BinanceCalls.AddressOf(market, mode)}/{(market == Market.Spot ? "api/v3/ticker/price" : "fapi/v2/ticker/price")}",
            $"{call.Method} {call.Endpoint}");
        Assert.Equal([("symbol", Symbol)], call.Query);
        Assert.False(call.Headers.ContainsKey("X-MBX-APIKEY"));
        Assert.Null(call.Body);
    }

    [Theory]
    [MemberData(nameof(BinanceCalls.MarketsAndEnvironments), MemberType = typeof(BinanceCalls))]
    public async Task The_symbols_of_a_market_are_read_with_one_call_for_all_of_them(Market market, TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var symbols = await data.GetSymbolsAsync(market, mode, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, symbols.Outcome);
        Assert.Equal(BinanceCalls.Now, symbols.ReadAt);

        // The Spot market is asked without the permission sets of each symbol, which are most
        // of its answer and are not read. The Futures market takes no parameter.
        var call = Assert.Single(binance.Calls);
        Assert.Equal(
            $"GET {BinanceCalls.AddressOf(market, mode)}/{(market == Market.Spot ? "api/v3/exchangeInfo" : "fapi/v1/exchangeInfo")}",
            $"{call.Method} {call.Endpoint}");
        Assert.Equal(market == Market.Spot ? [("showPermissionSets", "false")] : [], call.Query);
        Assert.False(call.Headers.ContainsKey("X-MBX-APIKEY"));
        Assert.Null(call.Body);
    }

    [Fact]
    public async Task The_symbols_of_the_configuration_file_that_the_Spot_market_has_are_given_with_their_rules()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var symbols = await data.GetSymbolsAsync(Market.Spot, TradingMode.Live, TestContext.Current.CancellationToken);

        // In the order of the file, and not of Binance's answer, and without the symbol that the
        // file does not list. The least value of an order is read under both of its names, and
        // a symbol that is not traded for the moment says so. A symbol takes a stop when it
        // names the stop-loss that is placed at the market among its types of order, and one
        // that does not name it takes none (FR-RSK-7). It takes the pair of a stop-loss and a
        // take-profit when it says so and takes both of them (FR-BIN-S2), and a take-profit
        // as an order of its own when it names that among its types of order.
        Assert.Equal(
            [
                new ExchangeSymbol(
                    "BTCUSDT", "BTC", "USDT", Trading: true,
                    PriceStep: 0.01m, QuantityStep: 0.00001m, MinQuantity: 0.00001m, MaxQuantity: 9000m, MinValue: 5m, StopOrders: true,
                    PairOrders: true, TakeProfitOrders: true),
                new ExchangeSymbol(
                    "SOLUSDT", "SOL", "USDT", Trading: false,
                    PriceStep: 0.01m, QuantityStep: 0.001m, MinQuantity: 0.001m, MaxQuantity: 90000m, MinValue: 5m, StopOrders: false,
                    PairOrders: false, TakeProfitOrders: false),
            ],
            symbols.Symbols);
    }

    [Fact]
    public async Task A_symbol_that_the_Futures_market_lacks_is_not_among_its_symbols_and_only_a_perpetual_contract_is_one()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var symbols = await data.GetSymbolsAsync(Market.Futures, TradingMode.Demo, TestContext.Current.CancellationToken);

        // The file lists two symbols, and the market has one of them. It takes a stop and a
        // take-profit, which the Futures market names in its own way, and no pair: only the
        // Spot market has one (FR-BIN-F3).
        Assert.Equal(
            [
                new ExchangeSymbol(
                    "BTCUSDT", "BTC", "USDT", Trading: true,
                    PriceStep: 0.1m, QuantityStep: 0.001m, MinQuantity: 0.001m, MaxQuantity: 1000m, MinValue: 100m, StopOrders: true,
                    PairOrders: false, TakeProfitOrders: true),
            ],
            symbols.Symbols);
    }

    [Fact]
    public async Task A_contract_of_the_Futures_market_that_is_delivered_is_not_the_symbol_of_its_name()
    {
        // The name of a symbol of the file, on a contract that is not perpetual.
        const string answer =
            """
            { "symbols": [ { "symbol": "SOLUSDT", "contractType": "CURRENT_QUARTER", "status": "TRADING", "baseAsset": "SOL",
                "quoteAsset": "USDT", "filters": [] } ] }
            """;
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var symbols = await data.GetSymbolsAsync(Market.Futures, TradingMode.Live, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, symbols.Outcome);
        Assert.Empty(symbols.Symbols);
    }

    [Theory]

    // Not the list of a market, and a row without a name, which could be any symbol.
    [InlineData("""[ { "symbol": "BTCUSDT" } ]""")]
    [InlineData("""{ "symbols": { "symbol": "BTCUSDT" } }""")]
    [InlineData("""{ "symbols": [ { "status": "TRADING" } ] }""")]

    // A symbol of the file without its assets, without rules, and without one of its rules.
    [InlineData("""{ "symbols": [ { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "filters": [] } ] }""")]
    [InlineData("""{ "symbols": [ { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDT" } ] }""")]
    [InlineData(
        """
        { "symbols": [ { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDT", "filters": [
            { "filterType": "PRICE_FILTER", "tickSize": "0.01" }, { "filterType": "LOT_SIZE", "minQty": "0.1", "maxQty": "9", "stepSize": "0.1" } ] } ] }
        """)]
    [InlineData(
        """
        { "symbols": [ { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDT", "filters": [
            { "filterType": "PRICE_FILTER", "tickSize": 0.01 }, { "filterType": "LOT_SIZE", "minQty": "0.1", "maxQty": "9", "stepSize": "0.1" },
            { "filterType": "NOTIONAL", "minNotional": "5" } ] } ] }
        """)]

    // A symbol of the file that is there twice: which rules an order must keep is not known.
    [InlineData(
        """
        { "symbols": [
          { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDT", "filters": [
            { "filterType": "PRICE_FILTER", "tickSize": "0.01" }, { "filterType": "LOT_SIZE", "minQty": "0.1", "maxQty": "9", "stepSize": "0.1" },
            { "filterType": "NOTIONAL", "minNotional": "5" } ] },
          { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDT", "filters": [
            { "filterType": "PRICE_FILTER", "tickSize": "0.10" }, { "filterType": "LOT_SIZE", "minQty": "0.1", "maxQty": "9", "stepSize": "0.1" },
            { "filterType": "NOTIONAL", "minNotional": "5" } ] } ] }
        """)]
    public async Task Symbols_that_do_not_read_in_whole_are_a_failure_and_no_part_of_them_is_given(string answer)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var symbols = await data.GetSymbolsAsync(Market.Spot, TradingMode.Live, TestContext.Current.CancellationToken);

        // A list without a symbol would say that the market does not have it.
        Assert.Equal(ExchangeCallOutcome.Failed, symbols.Outcome);
        Assert.Empty(symbols.Symbols);
        Assert.Null(symbols.ReadAt);
        Assert.Equal(
            "NotExpected", Assert.Single(application.Events(), written => written.Area == BinanceCalls.GatewayArea).Text("Reason"));
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task The_mark_price_of_a_symbol_of_the_Futures_market_is_read_with_its_funding(TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        // A funding rate below zero is one at which shorts pay longs. The next funding is at 16:00.
        Assert.Equal(
            new ExchangeMarkPrice(
                ExchangeCallOutcome.Succeeded,
                MarkPrice: 104250.64871234m,
                FundingRate: -0.00003m,
                NextFundingAt: new DateTimeOffset(2026, 11, 15, 16, 0, 0, TimeSpan.Zero),
                ReadAt: BinanceCalls.Now),
            await data.GetMarkPriceAsync(mode, Symbol, TestContext.Current.CancellationToken));

        var call = Assert.Single(binance.Calls);
        Assert.Equal($"GET {BinanceCalls.AddressOf(Market.Futures, mode)}/fapi/v1/premiumIndex", $"{call.Method} {call.Endpoint}");
        Assert.Equal([("symbol", Symbol)], call.Query);
        Assert.False(call.Headers.ContainsKey("X-MBX-APIKEY"));
        Assert.Null(call.Body);
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task The_open_interest_of_a_symbol_of_the_Futures_market_is_read(TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        Assert.Equal(
            new ExchangeOpenInterest(ExchangeCallOutcome.Succeeded, 10659.509m, BinanceCalls.Now),
            await data.GetOpenInterestAsync(mode, Symbol, TestContext.Current.CancellationToken));

        var call = Assert.Single(binance.Calls);
        Assert.Equal($"GET {BinanceCalls.AddressOf(Market.Futures, mode)}/fapi/v1/openInterest", $"{call.Method} {call.Endpoint}");
        Assert.Equal([("symbol", Symbol)], call.Query);
        Assert.False(call.Headers.ContainsKey("X-MBX-APIKEY"));
        Assert.Null(call.Body);
    }

    [Theory]

    // Not what Binance sends, an amount that is a number and not text, and nothing as a price.
    [InlineData("""[ "104250.01" ]""")]
    [InlineData("""{ "symbol": "BTCUSDT" }""")]
    [InlineData("""{ "price": 104250.01, "markPrice": 104250.01, "lastFundingRate": 0.0001, "nextFundingTime": 1794758400000, "openInterest": 10 }""")]
    [InlineData("""{ "price": "0", "markPrice": "0", "lastFundingRate": "0.0001", "nextFundingTime": 1794758400000, "openInterest": "ten" }""")]

    // A mark price without its funding, or without the time of the next one.
    [InlineData("""{ "price": "many", "markPrice": "104250.01", "nextFundingTime": 1794758400000, "openInterest": "-1" }""")]
    [InlineData("""{ "price": "1,5", "markPrice": "104250.01", "lastFundingRate": "0.0001", "nextFundingTime": "soon", "openInterest": "1e3" }""")]
    public async Task A_figure_that_does_not_read_is_a_failure_and_nothing_is_given(string answer)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        Assert.Equal(
            new ExchangePrice(ExchangeCallOutcome.Failed, null, null),
            await data.GetPriceAsync(Market.Spot, TradingMode.Live, Symbol, TestContext.Current.CancellationToken));
        Assert.Equal(
            new ExchangePrice(ExchangeCallOutcome.Failed, null, null),
            await data.GetPriceAsync(Market.Futures, TradingMode.Live, Symbol, TestContext.Current.CancellationToken));
        Assert.Equal(
            new ExchangeMarkPrice(ExchangeCallOutcome.Failed, null, null, null, null),
            await data.GetMarkPriceAsync(TradingMode.Live, Symbol, TestContext.Current.CancellationToken));
        Assert.Equal(
            new ExchangeOpenInterest(ExchangeCallOutcome.Failed, null, null),
            await data.GetOpenInterestAsync(TradingMode.Live, Symbol, TestContext.Current.CancellationToken));

        Assert.Equal(4, application.Events().Count(written => written.Area == BinanceCalls.GatewayArea && written.Text("Reason") == "NotExpected"));
    }

    [Theory]

    // A fault of the exchange, no connection, an answer that takes too long, and the signal of
    // too many requests, after which the limiter sends nothing more.
    [InlineData("Fault", ExchangeCallOutcome.Failed)]
    [InlineData("NoConnection", ExchangeCallOutcome.Failed)]
    [InlineData("TooLong", ExchangeCallOutcome.TimedOut)]
    [InlineData("TooMany", ExchangeCallOutcome.RateLimited)]
    public async Task A_read_that_gives_no_answer_is_an_outcome_with_nothing_and_does_not_fail(string how, ExchangeCallOutcome outcome)
    {
        var binance = new StandInSystem(_ => how switch
        {
            "Fault" => StandInSystem.Json(HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }"""),
            "NoConnection" => throw new HttpRequestException("The connection was refused."),
            "TooLong" => throw new TaskCanceledException("Binance took too long."),
            _ => StandInSystem.Json(HttpStatusCode.TooManyRequests, """{ "code": -1003, "msg": "Too much request weight used." }"""),
        });
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        foreach (var market in new[] { Market.Spot, Market.Futures })
        {
            var candles = await data.GetCandlesAsync(
                market, TradingMode.Demo, Symbol, CandleInterval.FourHours, TestContext.Current.CancellationToken);
            Assert.Equal((outcome, 0, null), (candles.Outcome, candles.Candles.Count, candles.ReadAt));

            var symbols = await data.GetSymbolsAsync(market, TradingMode.Demo, TestContext.Current.CancellationToken);
            Assert.Equal((outcome, 0, null), (symbols.Outcome, symbols.Symbols.Count, symbols.ReadAt));

            Assert.Equal(
                new ExchangePrice(outcome, null, null),
                await data.GetPriceAsync(market, TradingMode.Demo, Symbol, TestContext.Current.CancellationToken));
        }

        Assert.Equal(
            new ExchangeMarkPrice(outcome, null, null, null, null),
            await data.GetMarkPriceAsync(TradingMode.Demo, Symbol, TestContext.Current.CancellationToken));
        Assert.Equal(
            new ExchangeOpenInterest(outcome, null, null),
            await data.GetOpenInterestAsync(TradingMode.Demo, Symbol, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task What_is_no_call_ends_with_an_error_and_reaches_binance_with_nothing()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Only the symbols of the configuration file are served: not another symbol that
        // Binance has, not one in another case, and not text that is no symbol at all.
        foreach (var symbol in new[] { "ETHUSDT", "btcusdt", "BTCUSDT&symbol=ETHUSDT", string.Empty })
        {
            var refused = await Assert.ThrowsAsync<ArgumentException>(
                () => data.GetCandlesAsync(Market.Spot, TradingMode.Live, symbol, CandleInterval.OneHour, cancellationToken));

            // It is said without what was given.
            Assert.DoesNotContain("ETHUSDT", refused.Message);

            await Assert.ThrowsAsync<ArgumentException>(() => data.GetPriceAsync(Market.Futures, TradingMode.Demo, symbol, cancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => data.GetMarkPriceAsync(TradingMode.Live, symbol, cancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => data.GetOpenInterestAsync(TradingMode.Demo, symbol, cancellationToken));
        }

        await Assert.ThrowsAsync<ArgumentNullException>(() => data.GetPriceAsync(Market.Spot, TradingMode.Live, null!, cancellationToken));

        // A market, an environment, or a length of a candle that does not exist.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => data.GetCandlesAsync((Market)7, TradingMode.Live, Symbol, CandleInterval.OneHour, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => data.GetCandlesAsync(Market.Spot, (TradingMode)7, Symbol, CandleInterval.OneHour, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => data.GetCandlesAsync(Market.Spot, TradingMode.Live, Symbol, (CandleInterval)7, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => data.GetPriceAsync((Market)7, TradingMode.Live, Symbol, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => data.GetSymbolsAsync((Market)7, TradingMode.Live, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => data.GetSymbolsAsync(Market.Futures, (TradingMode)7, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => data.GetMarkPriceAsync((TradingMode)7, Symbol, cancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => data.GetOpenInterestAsync((TradingMode)7, Symbol, cancellationToken));

        Assert.Empty(binance.Calls);
    }

    [Fact]
    public async Task Each_read_passes_the_limiter_as_a_call_of_the_service_and_costs_the_weight_that_binance_documents()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();
        var limits = application.Services.GetRequiredService<IExchangeLimits>();

        int Used(Market market) =>
            limits.Usage().Single(limit => limit.Name == market.ToString() && limit.Environment == TradingMode.Live).Used;

        // On the Spot market: candles 2, a price 2, and the symbols 20.
        await data.GetCandlesAsync(Market.Spot, TradingMode.Live, Symbol, CandleInterval.OneDay, TestContext.Current.CancellationToken);
        Assert.Equal(2, Used(Market.Spot));
        await data.GetPriceAsync(Market.Spot, TradingMode.Live, Symbol, TestContext.Current.CancellationToken);
        Assert.Equal(4, Used(Market.Spot));
        await data.GetSymbolsAsync(Market.Spot, TradingMode.Live, TestContext.Current.CancellationToken);
        Assert.Equal(24, Used(Market.Spot));

        // On the Futures market: candles 2, which is what Binance counts for 499 rows, and 1
        // for each of a price, the symbols, the mark price, and the open interest.
        await data.GetCandlesAsync(Market.Futures, TradingMode.Live, Symbol, CandleInterval.OneDay, TestContext.Current.CancellationToken);
        Assert.Equal(2, Used(Market.Futures));
        await data.GetPriceAsync(Market.Futures, TradingMode.Live, Symbol, TestContext.Current.CancellationToken);
        await data.GetSymbolsAsync(Market.Futures, TradingMode.Live, TestContext.Current.CancellationToken);
        await data.GetMarkPriceAsync(TradingMode.Live, Symbol, TestContext.Current.CancellationToken);
        await data.GetOpenInterestAsync(TradingMode.Live, Symbol, TestContext.Current.CancellationToken);
        Assert.Equal(6, Used(Market.Futures));

        // The other environment has budgets of its own, of which nothing is used.
        Assert.All(limits.Usage().Where(limit => limit.Environment == TradingMode.Demo), limit => Assert.Equal(0, limit.Used));
    }

    [Fact]
    public async Task What_the_price_of_the_Futures_market_says_binance_has_counted_is_not_gone_by()
    {
        // Binance says of this one call that what its answer has counted is not accurate.
        var binance = BinanceCalls.ThatAnswers(answer => answer.Headers.Add("X-MBX-USED-WEIGHT-1M", "7"));
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();
        var limits = application.Services.GetRequiredService<IExchangeLimits>();

        ExchangeLimitUsage Of(Market market) =>
            limits.Usage().Single(limit => limit.Name == market.ToString() && limit.Environment == TradingMode.Live);

        await data.GetPriceAsync(Market.Futures, TradingMode.Live, Symbol, TestContext.Current.CancellationToken);
        Assert.Equal((1, null), (Of(Market.Futures).Used, Of(Market.Futures).Reported));

        // Every other answer is gone by: the price of the Spot market, and the mark price.
        await data.GetPriceAsync(Market.Spot, TradingMode.Live, Symbol, TestContext.Current.CancellationToken);
        Assert.Equal((2, 7), (Of(Market.Spot).Used, Of(Market.Spot).Reported));
        await data.GetMarkPriceAsync(TradingMode.Live, Symbol, TestContext.Current.CancellationToken);
        Assert.Equal((2, 7), (Of(Market.Futures).Used, Of(Market.Futures).Reported));
    }

    [Fact]
    public async Task A_read_is_logged_as_any_call_to_binance_is_and_the_gateway_adds_nothing_to_one_that_went_well()
    {
        var binance = BinanceCalls.ThatAnswers(answer => answer.Headers.Add("X-MBX-USED-WEIGHT-1M", "21"));
        using var application = BinanceCalls.Behind(binance);
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        await data.GetCandlesAsync(Market.Spot, TradingMode.Live, Symbol, CandleInterval.FifteenMinutes, TestContext.Current.CancellationToken);
        await data.GetSymbolsAsync(Market.Spot, TradingMode.Live, TestContext.Current.CancellationToken);
        await data.GetMarkPriceAsync(TradingMode.Demo, Symbol, TestContext.Current.CancellationToken);

        // With its duration, its outcome, and the request weight that Binance reports (NFR-LOG-5).
        var calls = application.Events().Where(written => written.Area == BinanceCalls.HttpArea).ToList();
        Assert.Equal(["/api/v3/klines", "/api/v3/exchangeInfo", "/fapi/v1/premiumIndex"], calls.Select(call => call.Text("Path")));
        Assert.All(calls, call =>
        {
            Assert.Equal("Information", call.Level);
            Assert.Equal("Succeeded", call.Text("Outcome"));
            Assert.InRange(call["DurationMs"].GetDouble(), 0, 60_000);
            Assert.Equal("21", call["Usage"].GetProperty("X-MBX-USED-WEIGHT-1M").GetString());
        });

        Assert.DoesNotContain(application.Events(), written => written.Area == BinanceCalls.GatewayArea);
    }

    [Fact]
    public void A_reading_holds_what_was_read_when_it_succeeded_and_only_then()
    {
        var at = BinanceCalls.Now;
        var candle = new ExchangeCandle(at.AddHours(-1), at, 1m, 2m, 0.5m, 1.5m, 10m);
        var symbol = new ExchangeSymbol("BTCUSDT", "BTC", "USDT", true, 0.01m, 0.001m, 0.001m, 9000m, 5m, StopOrders: true, PairOrders: true, TakeProfitOrders: true);

        Assert.Equal([candle], new ExchangeCandles(ExchangeCallOutcome.Succeeded, [candle], at).Candles);
        Assert.Throws<ArgumentException>(() => new ExchangeCandles(ExchangeCallOutcome.Failed, [candle], null));
        Assert.Throws<ArgumentException>(() => new ExchangeCandles(ExchangeCallOutcome.Succeeded, [], null));
        Assert.Throws<ArgumentException>(() => new ExchangeCandles(ExchangeCallOutcome.TimedOut, [], at));

        Assert.Equal([symbol], new ExchangeSymbols(ExchangeCallOutcome.Succeeded, [symbol], at).Symbols);
        Assert.Throws<ArgumentException>(() => new ExchangeSymbols(ExchangeCallOutcome.RateLimited, [symbol], null));
        Assert.Throws<ArgumentException>(() => new ExchangeSymbols(ExchangeCallOutcome.Succeeded, [symbol], null));

        Assert.Equal(1.5m, new ExchangePrice(ExchangeCallOutcome.Succeeded, 1.5m, at).Price);
        Assert.Throws<ArgumentException>(() => new ExchangePrice(ExchangeCallOutcome.Succeeded, null, at));
        Assert.Throws<ArgumentException>(() => new ExchangePrice(ExchangeCallOutcome.Succeeded, 1.5m, null));
        Assert.Throws<ArgumentException>(() => new ExchangePrice(ExchangeCallOutcome.Failed, 1.5m, null));

        Assert.Equal(at, new ExchangeMarkPrice(ExchangeCallOutcome.Succeeded, 1.5m, -0.0001m, at, at).NextFundingAt);
        Assert.Throws<ArgumentException>(() => new ExchangeMarkPrice(ExchangeCallOutcome.Succeeded, 1.5m, null, at, at));
        Assert.Throws<ArgumentException>(() => new ExchangeMarkPrice(ExchangeCallOutcome.Succeeded, 1.5m, 0m, null, at));
        Assert.Throws<ArgumentException>(() => new ExchangeMarkPrice(ExchangeCallOutcome.TimedOut, null, null, at, null));

        Assert.Equal(0m, new ExchangeOpenInterest(ExchangeCallOutcome.Succeeded, 0m, at).OpenInterest);
        Assert.Throws<ArgumentException>(() => new ExchangeOpenInterest(ExchangeCallOutcome.Failed, 10m, null));
    }
}
