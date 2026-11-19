using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// The system checks the margin state of open positions at each reconciliation and alerts the
/// user when a position approaches liquidation. It never adds margin on its own (FR-BIN-F5).
/// This is the gateway's part of it: it reads the position that an account holds in a symbol of
/// the Futures market, with its mark price and the price that Binance liquidates it at, which is
/// where its margin runs out, and it has nothing that adds margin. See "The position of an account, and its margin" in
/// docs/exchange.md.
/// </summary>
public class FuturesPositionTests
{
    private const string PositionsPath = "/fapi/v3/positionRisk";

    [Theory]
    [InlineData(TradingMode.Live)]
    [InlineData(TradingMode.Demo)]
    public async Task The_position_of_an_account_is_read_with_its_margin_in_one_signed_call_that_changes_nothing(TradingMode mode)
    {
        var binance = Answering($"[{BinanceCalls.PositionOf("0.030", liquidationPrice: "98120.5")}]");
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

        var position = await exchange.GetFuturesPositionAsync(
            BinanceCalls.User, mode, apiKey, secretKey, MarketService.Symbol, TestContext.Current.CancellationToken);

        // The positions of the account in the symbol, and nothing else: one call, which reads,
        // at the address of the Futures market in the environment of the credential.
        var call = Assert.Single(binance.Calls);
        Assert.Equal($"GET {BinanceCalls.AddressOf(Market.Futures, mode)}{PositionsPath}", $"{call.Method} {call.Endpoint}");
        Assert.Equal(["symbol", "recvWindow", "timestamp", "signature"], call.Query.Select(parameter => parameter.Name));
        Assert.Equal(("symbol", MarketService.Symbol), call.Query[0]);
        Assert.Null(call.Body);
        Assert.Equal(apiKey, call.Headers["X-MBX-APIKEY"]);
        Assert.Equal(BinanceCalls.SignatureOf(call, secretKey), call.Query[^1].Value);

        // What it holds, the price that it was entered at, the mark price, and the price that
        // Binance liquidates it at, which is where its margin runs out.
        Assert.Equal(
            new ExchangeFuturesPosition(
                ExchangeCallOutcome.Succeeded, Quantity: 0.030m, EntryPrice: 104250.0m, MarkPrice: 104250.64871234m, LiquidationPrice: 98120.5m),
            position);
        Assert.True(position.Holds);
    }

    [Theory]
    [InlineData("short", "-0.030", "111800.25")]
    [InlineData("unleveraged", "0.030", null)]
    [InlineData("one side of two", "-0.010", "111800.25")]
    public async Task A_position_is_said_as_Binance_says_it(string positions, string quantity, string? liquidationPrice)
    {
        // A short position, whose amount is below zero. One that Binance names no liquidation
        // price for, which it writes as nothing. And in hedge mode, where a symbol has a row for
        // each side, the one side that holds something.
        var rows = positions switch
        {
            "short" => $"[{BinanceCalls.PositionOf("-0.030", liquidationPrice: "111800.25")}]",
            "unleveraged" => $"[{BinanceCalls.PositionOf("0.030")}]",
            _ => $"[{BinanceCalls.PositionOf("0", side: "LONG")},{BinanceCalls.PositionOf("-0.010", side: "SHORT", liquidationPrice: "111800.25")}]",
        };

        var position = await ReadAsync(Answering(rows));

        Assert.Equal(ExchangeCallOutcome.Succeeded, position.Outcome);
        Assert.Equal(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture), position.Quantity);
        Assert.Equal(
            liquidationPrice is null ? null : decimal.Parse(liquidationPrice, System.Globalization.CultureInfo.InvariantCulture),
            position.LiquidationPrice);
        Assert.Equal((104250.0m, 104250.64871234m), (position.EntryPrice, position.MarkPrice));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("orders")]
    [InlineData("other")]
    public async Task An_account_that_holds_nothing_in_the_symbol_holds_no_position(string positions)
    {
        // No row at all, a symbol that has orders and no position, and a position in another symbol.
        var rows = positions switch
        {
            "none" => "[]",
            "orders" => $"[{BinanceCalls.PositionOf("0.000", liquidationPrice: "98120.5")}]",
            _ => $"[{BinanceCalls.PositionOf("5", "ETHUSDT", liquidationPrice: "2900.1")}]",
        };

        var position = await ReadAsync(Answering(rows));

        Assert.Equal(ExchangeFuturesPosition.Without(ExchangeCallOutcome.Succeeded), position);
        Assert.False(position.Holds);
    }

    [Fact]
    public async Task A_position_is_read_from_what_it_holds_and_its_three_prices_and_from_nothing_else()
    {
        // What warns goes by the prices. A row that says no more than them is a position.
        var position = await ReadAsync(Answering(
            """
            [ { "symbol": "BTCUSDT", "positionSide": "BOTH", "positionAmt": "0.030", "entryPrice": "104250.0",
                "markPrice": "101000.5", "liquidationPrice": "98120.5" } ]
            """));

        Assert.Equal(new ExchangeFuturesPosition(ExchangeCallOutcome.Succeeded, 0.030m, 104250.0m, 101000.5m, 98120.5m), position);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{ "positionAmt": "0.030" }""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "positionAmt": "some" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "positionAmt": "0.030", "markPrice": "101000.5", "liquidationPrice": "98120.5" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "positionAmt": "0.030", "entryPrice": "104250.0", "liquidationPrice": "98120.5" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "positionAmt": "0.030", "entryPrice": "104250.0", "markPrice": "101000.5" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "positionAmt": "0.030", "entryPrice": "104250.0", "markPrice": "0", "liquidationPrice": "98120.5" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "positionAmt": "0.030", "entryPrice": "104250.0", "markPrice": "101000.5", "liquidationPrice": "-1" } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""", ExchangeCallOutcome.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, """{ "code": -1003, "msg": "Too much request weight used." }""", ExchangeCallOutcome.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }""", ExchangeCallOutcome.Failed)]
    public async Task A_position_that_cannot_be_read_is_not_one_that_is_far_from_liquidation(
        HttpStatusCode status, string body, ExchangeCallOutcome expected)
    {
        // A list that is no list, a row that does not read, a position without one of its
        // prices, and a call that Binance does not answer as asked: nothing is said of the
        // position, and what checks its margin checks again.
        var position = await ReadAsync(new StandInSystem(_ => StandInSystem.Json(status, body)));

        Assert.Equal(ExchangeFuturesPosition.Without(expected), position);
    }

    [Fact]
    public async Task A_long_and_a_short_position_in_one_symbol_are_not_read_as_one_position()
    {
        // In hedge mode an account can hold both. The service trades in one-way mode, and does
        // not make one position of the two.
        var position = await ReadAsync(Answering(
            $"[{BinanceCalls.PositionOf("0.020", side: "LONG", liquidationPrice: "98120.5")},{BinanceCalls.PositionOf("-0.010", side: "SHORT", liquidationPrice: "111800.25")}]"));

        Assert.Equal(ExchangeFuturesPosition.Without(ExchangeCallOutcome.Failed), position);
    }

    [Fact]
    public void A_reading_says_a_position_when_it_succeeded_and_only_then()
    {
        Assert.Throws<ArgumentException>(() => new ExchangeFuturesPosition(ExchangeCallOutcome.Failed, 0.03m, 104250m, 104000m, 98000m));
        Assert.Throws<ArgumentException>(() => new ExchangeFuturesPosition(ExchangeCallOutcome.Succeeded, 0.03m, null, 104000m, 98000m));
        Assert.Throws<ArgumentException>(() => new ExchangeFuturesPosition(ExchangeCallOutcome.Succeeded, 0m, null, 104000m, null));
        Assert.False(ExchangeFuturesPosition.Without(ExchangeCallOutcome.RateLimited).Holds);
    }

    private static async Task<ExchangeFuturesPosition> ReadAsync(StandInSystem binance)
    {
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        return await exchange.GetFuturesPositionAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, TestContext.Current.CancellationToken);
    }

    /// <summary>A Binance that answers every call with the positions that the test gives.</summary>
    private static StandInSystem Answering(string positions) => new(_ => StandInSystem.Json(HttpStatusCode.OK, positions));
}
