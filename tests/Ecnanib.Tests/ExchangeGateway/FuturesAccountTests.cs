using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.ExchangeGateway.Trading;
using Ecnanib.Shared;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Futures agents use isolated margin and one-way position mode. The system sets margin type and
/// leverage for the agent's symbol before entry. It does not change account-wide settings; if
/// the account is in a conflicting mode or already holds a position in the symbol, the agent
/// does not start and the user is told what to change (FR-BIN-F2). This is the gateway's part
/// of it: it reads how an account stands, and sets the two settings of a symbol. See "The
/// account of Futures" in docs/exchange.md.
/// </summary>
public class FuturesAccountTests
{
    private const string AccountPath = "/fapi/v1/accountConfig";
    private const string PositionsPath = "/fapi/v3/positionRisk";
    private const string MarginPath = "/fapi/v1/marginType";
    private const string LeveragePath = "/fapi/v1/leverage";

    [Theory]
    [InlineData(TradingMode.Live)]
    [InlineData(TradingMode.Demo)]
    public async Task How_an_account_stands_is_read_with_two_signed_calls_that_change_nothing(TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

        var account = await exchange.GetFuturesAccountAsync(
            BinanceCalls.User, mode, apiKey, secretKey, MarketService.Symbol, TestContext.Current.CancellationToken);

        // The settings of the account, and then its positions in the symbol: both read, at the
        // address of the Futures market in the environment of the credential.
        Assert.Equal(
            [$"GET {BinanceCalls.AddressOf(Market.Futures, mode)}{AccountPath}", $"GET {BinanceCalls.AddressOf(Market.Futures, mode)}{PositionsPath}"],
            binance.Calls.Select(call => $"{call.Method} {call.Endpoint}"));
        Assert.Equal(("symbol", MarketService.Symbol), binance.Calls[1].Query[0]);
        Assert.All(binance.Calls, call =>
        {
            Assert.Null(call.Body);
            Assert.Equal(apiKey, call.Headers["X-MBX-APIKEY"]);
            Assert.Equal(BinanceCalls.SignatureOf(call, secretKey), call.Query[^1].Value);
        });

        // One-way mode, one asset, and nothing held in the symbol: nothing stands in the way.
        Assert.Equal(new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, HedgeMode: false, MultiAssetsMargin: false, HoldsPosition: false), account);
    }

    [Theory]
    [InlineData("""{ "canTrade": true, "dualSidePosition": true, "multiAssetsMargin": false }""", "[]", true, false, false)]
    [InlineData("""{ "canTrade": true, "dualSidePosition": false, "multiAssetsMargin": true }""", "[]", false, true, false)]
    [InlineData("""{ "canTrade": true, "dualSidePosition": false, "multiAssetsMargin": false }""", "long", false, false, true)]
    [InlineData("""{ "canTrade": true, "dualSidePosition": false, "multiAssetsMargin": false }""", "short", false, false, true)]
    [InlineData("""{ "canTrade": true, "dualSidePosition": false, "multiAssetsMargin": false }""", "orders", false, false, false)]
    [InlineData("""{ "canTrade": true, "dualSidePosition": false, "multiAssetsMargin": false }""", "other", false, false, false)]
    [InlineData("""{ "canTrade": true, "dualSidePosition": true, "multiAssetsMargin": false }""", "hedged", true, false, true)]
    public async Task The_modes_of_the_whole_account_and_a_position_in_the_symbol_are_said_as_Binance_says_them(
        string settings, string positions, bool hedge, bool multiAssets, bool holds)
    {
        // A position that is long, one that is short, a symbol that has orders and no position,
        // a position in another symbol, and in hedge mode a row for each side.
        var rows = positions switch
        {
            "long" => $"[{BinanceCalls.PositionOf("0.030")}]",
            "short" => $"[{BinanceCalls.PositionOf("-0.030")}]",
            "orders" => $"[{BinanceCalls.PositionOf("0.000")}]",
            "other" => $"[{BinanceCalls.PositionOf("5", "ETHUSDT")}]",
            "hedged" => $"[{BinanceCalls.PositionOf("0", side: "LONG")},{BinanceCalls.PositionOf("-0.010", side: "SHORT")}]",
            _ => positions,
        };

        var binance = Answering(path => path == AccountPath ? settings : rows);
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var account = await exchange.GetFuturesAccountAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, TestContext.Current.CancellationToken);

        Assert.Equal(new ExchangeFuturesAccount(ExchangeCallOutcome.Succeeded, hedge, multiAssets, holds), account);
    }

    [Theory]
    [InlineData(AccountPath, HttpStatusCode.OK, """{ "canTrade": true, "dualSidePosition": false }""", ExchangeCallOutcome.Failed)]
    [InlineData(AccountPath, HttpStatusCode.OK, """{ "canTrade": true, "dualSidePosition": "false", "multiAssetsMargin": false }""", ExchangeCallOutcome.Failed)]
    [InlineData(AccountPath, HttpStatusCode.Unauthorized, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""", ExchangeCallOutcome.AuthenticationFailed)]
    [InlineData(AccountPath, HttpStatusCode.TooManyRequests, """{ "code": -1003, "msg": "Too much request weight used." }""", ExchangeCallOutcome.RateLimited)]
    [InlineData(PositionsPath, HttpStatusCode.OK, """{ "positionAmt": "0.030" }""", ExchangeCallOutcome.Failed)]
    [InlineData(PositionsPath, HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "positionAmt": "some" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(PositionsPath, HttpStatusCode.OK, """[ { "symbol": "BTCUSDT" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(PositionsPath, HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }""", ExchangeCallOutcome.Failed)]
    public async Task An_account_that_cannot_be_read_is_not_one_that_nothing_stands_in_the_way_of(
        string path, HttpStatusCode status, string body, ExchangeCallOutcome expected)
    {
        // An account that does not say both of its modes, a list of positions that does not
        // read, and a call that Binance does not answer as asked: nothing is said of the account.
        var binance = new StandInSystem(request => request.RequestUri!.AbsolutePath == path
            ? StandInSystem.Json(status, body)
            : StandInSystem.Json(HttpStatusCode.OK, BinanceCalls.AnswerTo(request)));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var account = await exchange.GetFuturesAccountAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeFuturesAccount.Without(expected), account);

        // The positions are not asked for once the settings did not say.
        Assert.Equal(path == AccountPath ? 1 : 2, binance.Calls.Count);
    }

    [Fact]
    public void A_reading_says_how_an_account_stands_when_it_succeeded_and_only_then()
    {
        Assert.Throws<ArgumentException>(() => new ExchangeFuturesAccount(ExchangeCallOutcome.Failed, HedgeMode: true, MultiAssetsMargin: false, HoldsPosition: false));
        Assert.Throws<ArgumentException>(() => new ExchangeFuturesAccount(ExchangeCallOutcome.TimedOut, HedgeMode: false, MultiAssetsMargin: false, HoldsPosition: true));
        Assert.False(ExchangeFuturesAccount.Without(ExchangeCallOutcome.RateLimited).HoldsPosition);
    }

    [Theory]
    [InlineData(TradingMode.Live)]
    [InlineData(TradingMode.Demo)]
    public async Task The_margin_of_a_symbol_is_set_to_isolated_and_then_its_leverage_each_with_one_signed_call(TradingMode mode)
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

        var set = await orders.SetIsolatedMarginAsync(
            BinanceCalls.User, mode, apiKey, secretKey, MarketService.Symbol, leverage: 3, TestContext.Current.CancellationToken);

        // Two settings of the symbol, and none of the whole account: the margin, and the
        // leverage. Each names the symbol and what it is to have, and nothing else.
        Assert.Equal(ExchangeOrderOutcome.Answered, set);
        Assert.Equal(
            [$"POST {BinanceCalls.AddressOf(Market.Futures, mode)}{MarginPath}", $"POST {BinanceCalls.AddressOf(Market.Futures, mode)}{LeveragePath}"],
            binance.System.Calls.Select(call => $"{call.Method} {call.Endpoint}"));
        Assert.Equal([("marginType", "ISOLATED"), ("symbol", MarketService.Symbol), ("recvWindow", "5000")], binance.System.Calls[0].Query.Take(3));
        Assert.Equal([("symbol", MarketService.Symbol), ("leverage", "3"), ("recvWindow", "5000")], binance.System.Calls[1].Query.Take(3));
        Assert.All(binance.System.Calls, call =>
        {
            Assert.Null(call.Body);
            Assert.Equal(apiKey, call.Headers["X-MBX-APIKEY"]);
            Assert.Equal(BinanceCalls.SignatureOf(call, secretKey), call.Query[^1].Value);
        });
        Assert.True(binance.Isolated);
    }

    [Fact]
    public async Task A_symbol_that_has_isolated_margin_already_is_one_that_has_it()
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        await orders.SetIsolatedMarginAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, leverage: 2, TestContext.Current.CancellationToken);

        // Binance answers the second time with its error -4046, which is no refusal: the
        // leverage is set all the same.
        var again = await orders.SetIsolatedMarginAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, leverage: 4, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeOrderOutcome.Answered, again);
        Assert.Equal([MarginPath, LeveragePath, MarginPath, LeveragePath], binance.Changes);
        Assert.Equal([2, 4], binance.Leverages);
    }

    [Theory]
    [InlineData(MarginPath, HttpStatusCode.BadRequest, """{ "code": -4047, "msg": "Margin type cannot be changed if there exists open orders." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(MarginPath, HttpStatusCode.BadRequest, """{ "code": -4048, "msg": "Margin type cannot be changed if there exists position." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(MarginPath, HttpStatusCode.BadRequest, """{ "code": -4168, "msg": "Unable to adjust to isolated-margin mode under the Multi-Assets mode." }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(MarginPath, HttpStatusCode.Unauthorized, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""", ExchangeOrderOutcome.KeyRefused)]
    [InlineData(MarginPath, HttpStatusCode.TooManyRequests, """{ "code": -1003, "msg": "Too much request weight used." }""", ExchangeOrderOutcome.NotSent)]
    [InlineData(MarginPath, HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }""", ExchangeOrderOutcome.Unknown)]
    [InlineData(LeveragePath, HttpStatusCode.BadRequest, """{ "code": -4028, "msg": "Invalid leverage" }""", ExchangeOrderOutcome.Rejected)]
    [InlineData(LeveragePath, HttpStatusCode.OK, """{ "leverage": 20, "maxNotionalValue": "1000000", "symbol": "BTCUSDT" }""", ExchangeOrderOutcome.Unknown)]
    [InlineData(LeveragePath, HttpStatusCode.OK, """{ "code": 200, "msg": "success" }""", ExchangeOrderOutcome.Unknown)]
    [InlineData(LeveragePath, HttpStatusCode.ServiceUnavailable, "", ExchangeOrderOutcome.Unknown)]
    public async Task How_the_calls_that_set_the_margin_end_says_whether_it_is_set_and_no_call_is_made_again(
        string path, HttpStatusCode status, string body, ExchangeOrderOutcome expected)
    {
        var binance = new TradingBinance
        {
            Instead = request => request.RequestUri!.AbsolutePath == path ? StandInSystem.Json(status, body) : null,
        };
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var set = await orders.SetIsolatedMarginAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, leverage: 3, TestContext.Current.CancellationToken);

        // Only Binance's word that the symbol has the leverage that was asked for says that it
        // is set. A margin that Binance did not take is followed by no leverage.
        Assert.Equal(expected, set);
        Assert.Equal(path == MarginPath ? [MarginPath] : [MarginPath, LeveragePath], binance.Changes);
    }

    [Fact]
    public async Task A_leverage_below_one_is_an_error_of_the_caller_and_nothing_is_sent()
    {
        var binance = new TradingBinance();
        using var application = BinanceCalls.Behind(binance.System);
        var orders = application.Services.GetRequiredService<IExchangeOrders>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => orders.SetIsolatedMarginAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, leverage: 0, TestContext.Current.CancellationToken));

        Assert.Empty(binance.System.Calls);
    }

    /// <summary>A Binance that answers a call to a path with what the test gives for it.</summary>
    private static StandInSystem Answering(Func<string, string> answer) =>
        new(request => StandInSystem.Json(HttpStatusCode.OK, answer(request.RequestUri!.AbsolutePath)));
}
