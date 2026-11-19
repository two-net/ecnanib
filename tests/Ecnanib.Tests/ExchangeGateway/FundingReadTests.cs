using System.Globalization;
using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// The current funding rate and the next funding time are included in the data package, and
/// funding paid or received is recorded per position (FR-BIN-F4). This is the gateway's part of
/// the record: it reads what an account received and paid as funding in a symbol of the Futures
/// market, between two moments. The rate and the next funding time are read with the mark price:
/// see <see cref="MarketDataTests"/>. See "The funding of an account" in docs/exchange.md.
/// </summary>
public class FundingReadTests
{
    private const string FundingPath = "/fapi/v1/income";

    private static readonly DateTimeOffset Midnight = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TradingMode.Live)]
    [InlineData(TradingMode.Demo)]
    public async Task The_funding_of_an_account_in_a_symbol_is_read_with_one_signed_call_that_changes_nothing(TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);
        var from = Midnight.AddHours(-3).AddMilliseconds(250);
        var to = Midnight.AddHours(9).AddMilliseconds(999);

        var funding = await exchange.GetFundingAsync(
            BinanceCalls.User, mode, apiKey, secretKey, MarketService.Symbol, from, to, TestContext.Current.CancellationToken);

        // The income of the account that is funding, in the symbol, from the first moment to
        // the last, both to the millisecond, and as many rows as Binance gives in one answer.
        var call = Assert.Single(binance.Calls);
        Assert.Equal($"GET {BinanceCalls.AddressOf(Market.Futures, mode)}{FundingPath}", $"{call.Method} {call.Endpoint}");
        Assert.Equal(
            [
                ("incomeType", "FUNDING_FEE"),
                ("symbol", MarketService.Symbol),
                ("startTime", from.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
                ("endTime", to.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
                ("limit", "1000"),
                ("recvWindow", "5000"),
            ],
            call.Query.Take(6));
        Assert.Equal(["timestamp", "signature"], call.Query.Skip(6).Select(parameter => parameter.Name));
        Assert.Null(call.Body);
        Assert.Equal(apiKey, call.Headers["X-MBX-APIKEY"]);
        Assert.Equal(BinanceCalls.SignatureOf(call, secretKey), call.Query[^1].Value);

        // What the account paid is below zero, and what it received above it: each with the
        // number that Binance gave it, its asset, and its funding time.
        Assert.Equal(ExchangeCallOutcome.Succeeded, funding.Outcome);
        Assert.Equal(
            [
                new ExchangeFunding("9689322392", -0.375m, "USDT", Midnight),
                new ExchangeFunding("9689322418", 0.0125m, "USDT", Midnight.AddHours(8)),
            ],
            funding.Fundings);
        Assert.False(funding.More);
    }

    [Fact]
    public async Task The_funding_is_said_oldest_first_whatever_order_Binance_gives_it_in()
    {
        var funding = await ReadAsync(Answering(
            """
            [
              { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "0.01250000", "asset": "USDT", "time": 1794729600000, "tranId": 9689322418 },
              { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "-0.00200000", "asset": "USDC", "time": 1794700800000, "tranId": "9689322393" },
              { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "-0.37500000", "asset": "USDT", "time": 1794700800000, "tranId": 9689322392 }
            ]
            """));

        // Those of one moment in the order of their numbers. A number that Binance writes as
        // text is the same number.
        Assert.Equal(
            [("9689322392", -0.375m, "USDT"), ("9689322393", -0.002m, "USDC"), ("9689322418", 0.0125m, "USDT")],
            funding.Fundings.Select(row => (row.TransactionId, row.Amount, row.Asset)));
        Assert.Equal([Midnight, Midnight, Midnight.AddHours(8)], funding.Fundings.Select(row => row.At));
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    public async Task An_answer_of_as_many_rows_as_Binance_gives_at_most_may_not_be_all(int rows, bool more)
    {
        var body = "[" + string.Join(
            ",",
            Enumerable.Range(0, rows).Select(row =>
                $$"""{ "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "-0.01", "asset": "USDT", "time": {{1794700800000 + (row * 3600000L)}}, "tranId": {{7000 + row}} }""")) + "]";

        var funding = await ReadAsync(Answering(body));

        Assert.Equal((ExchangeCallOutcome.Succeeded, rows, more), (funding.Outcome, funding.Fundings.Count, funding.More));
    }

    [Fact]
    public async Task An_account_that_received_and_paid_nothing_in_the_time_has_no_funding()
    {
        var funding = await ReadAsync(Answering("[]"));

        Assert.Equal((ExchangeCallOutcome.Succeeded, 0, false), (funding.Outcome, funding.Fundings.Count, funding.More));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{ "income": "-0.375" }""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "some", "asset": "USDT", "time": 1794700800000, "tranId": 1 } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": -0.375, "asset": "USDT", "time": 1794700800000, "tranId": 1 } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "-0.375", "asset": "", "time": 1794700800000, "tranId": 1 } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "-0.375", "asset": "USDT", "time": "midnight", "tranId": 1 } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "-0.375", "asset": "USDT", "time": 1794700800000 } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "incomeType": "FUNDING_FEE", "income": "-0.375", "asset": "USDT", "time": 1794700800000, "tranId": 1.5 } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "BTCUSDT", "incomeType": "COMMISSION", "income": "-0.375", "asset": "USDT", "time": 1794700800000, "tranId": 1 } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.OK, """[ { "symbol": "ETHUSDT", "incomeType": "FUNDING_FEE", "income": "-0.375", "asset": "USDT", "time": 1794700800000, "tranId": 1 } ]""", ExchangeCallOutcome.Failed)]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""", ExchangeCallOutcome.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, """{ "code": -1003, "msg": "Too much request weight used." }""", ExchangeCallOutcome.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }""", ExchangeCallOutcome.Failed)]
    public async Task Funding_that_cannot_be_read_is_not_funding_that_was_not_paid(HttpStatusCode status, string body, ExchangeCallOutcome expected)
    {
        // A list that is no list, a row that does not read, a row that is no funding or is of
        // another symbol than the one that was asked for, and a call that Binance does not
        // answer as asked: a part of a list would make the rest look as if it had not been
        // paid, so nothing of it is said.
        var funding = await ReadAsync(new StandInSystem(_ => StandInSystem.Json(status, body)));

        Assert.Equal((expected, 0, false), (funding.Outcome, funding.Fundings.Count, funding.More));
    }

    [Fact]
    public async Task A_time_that_ends_before_it_begins_is_an_error_of_the_caller_and_nothing_is_sent()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        await Assert.ThrowsAsync<ArgumentException>(() => exchange.GetFundingAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, Midnight, Midnight.AddMilliseconds(-1),
            TestContext.Current.CancellationToken));

        Assert.Empty(binance.Calls);

        // One moment is a time: what was paid at it is asked for.
        await exchange.GetFundingAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, Midnight, Midnight, TestContext.Current.CancellationToken);

        Assert.Single(binance.Calls);
    }

    [Fact]
    public void A_reading_has_funding_when_it_succeeded_and_only_then()
    {
        var row = new ExchangeFunding("1", -0.1m, "USDT", Midnight);

        Assert.Throws<ArgumentException>(() => new ExchangeFundings(ExchangeCallOutcome.Failed, [row], More: false));
        Assert.Throws<ArgumentException>(() => new ExchangeFundings(ExchangeCallOutcome.TimedOut, [], More: true));
        Assert.Empty(ExchangeFundings.Without(ExchangeCallOutcome.RateLimited).Fundings);
    }

    private static async Task<ExchangeFundings> ReadAsync(StandInSystem binance)
    {
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        return await exchange.GetFundingAsync(
            BinanceCalls.User, TradingMode.Demo, apiKey, secretKey, MarketService.Symbol, Midnight.AddDays(-1), Midnight.AddDays(1),
            TestContext.Current.CancellationToken);
    }

    /// <summary>A Binance that answers every call with the income that the test gives.</summary>
    private static StandInSystem Answering(string income) => new(_ => StandInSystem.Json(HttpStatusCode.OK, income));
}
