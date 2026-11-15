using System.Diagnostics;
using System.Net;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// A call to Binance that gives no answer is not an error of the gateway: it ends as an outcome,
/// which a caller that may name no type of the network can act on (NFR-ARC-3). Only Binance's
/// answer to a wrong credential says that the credential should be checked again (FR-KEY-3).
/// What the log of outbound calls cannot see, the gateway says itself (NFR-LOG-5).
/// </summary>
public class ExchangeCallOutcomeTests
{
    private const string WrongKey = """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""";

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -2008, "msg": "Invalid Api-Key ID." }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -2014, "msg": "API-key format invalid." }""")]
    [InlineData(HttpStatusCode.Unauthorized, WrongKey)]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -1022, "msg": "Signature for this request is not valid." }""")]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": -1002, "msg": "You are not authorized to execute this request." }""")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    public async Task What_binance_answers_a_wrong_credential_with_says_that_the_credential_failed(HttpStatusCode status, string body)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(status, body));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);
            var balances = await exchange.GetBalancesAsync(
                BinanceCalls.User, market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

            Assert.Equal(ExchangeCallOutcome.AuthenticationFailed, balances.Outcome);
            Assert.Empty(balances.Balances);
        }
    }

    [Theory]

    // The clock of the service is off, which is not the user's key being wrong.
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -1021, "msg": "Timestamp for this request is outside of the recvWindow." }""")]

    // The firewall in front of the exchange, a call it does not have, and a fault of the exchange.
    // The limit on requests is an outcome of its own: see RequestLimitTests.
    [InlineData(HttpStatusCode.Forbidden, "<html>Forbidden</html>")]
    [InlineData(HttpStatusCode.NotFound, """{ "code": -5000, "msg": "Path is invalid." }""")]
    [InlineData(HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }""")]
    [InlineData(HttpStatusCode.BadGateway, "")]
    public async Task Any_other_answer_that_is_not_a_success_is_a_failure_and_says_nothing_about_a_credential(
        HttpStatusCode status, string body)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(status, body));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

            // A signed call, and one without a credential.
            Assert.Equal(
                ExchangeCallOutcome.Failed,
                (await exchange.GetBalancesAsync(
                    BinanceCalls.User, market, mode, apiKey, secretKey, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(
                new ExchangeTime(ExchangeCallOutcome.Failed, null),
                await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("""{ "serverTime": "1794562200250" }""", "NotExpected")]
    [InlineData("""{ "serverTime": 1e40 }""", "NotExpected")]
    [InlineData("""{ "time": 1794562200250 }""", "NotExpected")]
    [InlineData("""[ 1794562200250 ]""", "NotExpected")]
    [InlineData("<html>Maintenance</html>", "NotJson")]
    [InlineData("", "NotJson")]
    public async Task A_success_that_gives_no_time_is_a_failure_and_the_gateway_says_so(string answer, string reason)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        Assert.Equal(
            new ExchangeTime(ExchangeCallOutcome.Failed, null),
            await exchange.GetServerTimeAsync(Market.Futures, TradingMode.Live, TestContext.Current.CancellationToken));

        // The status says that the call went well, so the log of outbound calls does too. The
        // gateway says that it gave nothing, and why.
        var events = application.Events();
        Assert.Equal("Succeeded", Assert.Single(events, written => written.Area == BinanceCalls.HttpArea).Text("Outcome"));

        var said = Assert.Single(events, written => written.Area == BinanceCalls.GatewayArea);
        Assert.Equal("Warning", said.Level);
        Assert.Equal("fapi/v1/time", said.Text("Path"));
        Assert.Equal("Futures", said.Text("Market"));
        Assert.Equal("Live", said.Text("Environment"));
        Assert.Equal(reason, said.Text("Reason"));
    }

    [Theory]

    // Not the account, and an account without what it holds.
    [InlineData(Market.Spot, """[ { "asset": "BTC", "free": "1", "locked": "0" } ]""")]
    [InlineData(Market.Spot, """{ "canTrade": true }""")]
    [InlineData(Market.Spot, """{ "balances": { "asset": "BTC", "free": "1", "locked": "0" } }""")]

    // A row that does not read: no asset, an amount that is missing, and one that is no number.
    [InlineData(Market.Spot, """{ "balances": [ { "asset": "BTC", "free": "1", "locked": "0" }, { "free": "1", "locked": "0" } ] }""")]
    [InlineData(Market.Spot, """{ "balances": [ { "asset": "BTC", "free": "1" } ] }""")]
    [InlineData(Market.Spot, """{ "balances": [ { "asset": "BTC", "free": "plenty", "locked": "0" } ] }""")]
    [InlineData(Market.Spot, """{ "balances": [ { "asset": "BTC", "free": 1, "locked": 0 } ] }""")]

    // An asset that is there twice: which of the two it holds is not known.
    [InlineData(Market.Spot, """{ "balances": [ { "asset": "BTC", "free": "1", "locked": "0" }, { "asset": "BTC", "free": "2", "locked": "0" } ] }""")]

    // The same of the wallet of the Futures market, which is a list.
    [InlineData(Market.Futures, """{ "asset": "USDT", "balance": "1", "availableBalance": "1" }""")]
    [InlineData(Market.Futures, """[ { "asset": "USDT", "balance": "1", "availableBalance": "1" }, "USDC" ]""")]
    [InlineData(Market.Futures, """[ { "asset": "USDT", "balance": "1" } ]""")]
    [InlineData(Market.Futures, """[ { "asset": "", "balance": "1", "availableBalance": "1" } ]""")]
    [InlineData(Market.Futures, """[ { "asset": "USDT", "balance": "1,5", "availableBalance": "1" } ]""")]
    public async Task Balances_that_do_not_read_in_whole_are_a_failure_and_no_part_of_them_is_given(Market market, string answer)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var balances = await exchange.GetBalancesAsync(
            BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, TestContext.Current.CancellationToken);

        // A part of a list would make the rest look empty.
        Assert.Equal(ExchangeCallOutcome.Failed, balances.Outcome);
        Assert.Empty(balances.Balances);

        var said = Assert.Single(application.Events(), written => written.Area == BinanceCalls.GatewayArea);
        Assert.Equal("NotExpected", said.Text("Reason"));
    }

    [Fact]
    public async Task An_account_that_holds_nothing_has_no_balances_and_is_no_failure()
    {
        var binance = new StandInSystem(request => StandInSystem.Json(
            HttpStatusCode.OK, request.RequestUri!.AbsolutePath == "/api/v3/account" ? """{ "balances": [] }""" : "[]"));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        foreach (var market in new[] { Market.Spot, Market.Futures })
        {
            var balances = await exchange.GetBalancesAsync(
                BinanceCalls.User, market, TradingMode.Demo, apiKey, secretKey, TestContext.Current.CancellationToken);

            Assert.Equal(ExchangeCallOutcome.Succeeded, balances.Outcome);
            Assert.Empty(balances.Balances);
        }
    }

    [Fact]
    public async Task An_error_of_binance_is_logged_with_its_number_and_without_its_text()
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(
            HttpStatusCode.BadRequest, """{ "code": -1021, "msg": "Timestamp for this request is outside of the recvWindow." }"""));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        await exchange.GetBalancesAsync(
            BinanceCalls.User, Market.Futures, TradingMode.Demo, apiKey, secretKey, TestContext.Current.CancellationToken);

        // The log of outbound calls has the status, which most of Binance's errors share.
        var events = application.Events();
        var call = Assert.Single(events, written => written.Area == BinanceCalls.HttpArea);
        Assert.Equal("Failed", call.Text("Outcome"));
        Assert.Equal(400, call["StatusCode"].GetInt32());

        // The gateway says which error it is.
        var said = Assert.Single(events, written => written.Area == BinanceCalls.GatewayArea);
        Assert.Equal("Warning", said.Level);
        Assert.Equal(-1021, said["BinanceCode"].GetInt32());
        Assert.Equal("fapi/v3/balance", said.Text("Path"));
        Assert.Equal("Futures", said.Text("Market"));
        Assert.Equal("Demo", said.Text("Environment"));

        // What Binance wrote about it stays in the body, which is written at the debug level only.
        Assert.DoesNotContain("outside of the recvWindow", application.LogText());
    }

    [Fact]
    public async Task An_exchange_that_does_not_answer_is_an_outcome_and_the_call_does_not_fail()
    {
        Exception failure = new HttpRequestException("The connection was refused.");
        var binance = new StandInSystem(_ => throw failure);
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Live);

        Assert.Equal(
            new ExchangeTime(ExchangeCallOutcome.Failed, null),
            await exchange.GetServerTimeAsync(Market.Spot, TradingMode.Live, TestContext.Current.CancellationToken));
        Assert.Equal(
            ExchangeCallOutcome.Failed,
            (await exchange.GetBalancesAsync(
                BinanceCalls.User, Market.Futures, TradingMode.Live, apiKey, secretKey, TestContext.Current.CancellationToken))
            .Outcome);

        // An answer that breaks off while it is read ends the same way.
        failure = new IOException("The connection was closed.");
        Assert.Equal(
            ExchangeCallOutcome.Failed,
            (await exchange.GetBalancesAsync(
                BinanceCalls.User, Market.Spot, TradingMode.Live, apiKey, secretKey, TestContext.Current.CancellationToken))
            .Outcome);

        // A call that is given up, which nobody cancelled, took too long.
        failure = new TaskCanceledException("Binance took too long.");
        Assert.Equal(
            new ExchangeTime(ExchangeCallOutcome.TimedOut, null),
            await exchange.GetServerTimeAsync(Market.Futures, TradingMode.Demo, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_call_is_given_up_after_fifteen_seconds_of_the_applications_clock(bool signed)
    {
        // A Binance that never answers, and a clock that shows which limit the call was given.
        var clock = new TimerClock();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var binance = new StandInSystem(async (_, givenUp) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, givenUp);

            throw new UnreachableException();
        });
        using var application = BinanceCalls.Behind(binance, clock: clock);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Live);

        var call = signed
            ? OutcomeOfAsync(exchange.GetBalancesAsync(
                BinanceCalls.User, Market.Futures, TradingMode.Live, apiKey, secretKey, TestContext.Current.CancellationToken))
            : OutcomeOfAsync(exchange.GetServerTimeAsync(Market.Spot, TradingMode.Live, TestContext.Current.CancellationToken));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var limit = TimeSpan.FromSeconds(15);
        Assert.Contains(limit, clock.Timers);
        Assert.False(call.IsCompleted);

        // When it runs out, the call ends: as an outcome, and not as an error.
        clock.RunOut(limit);

        Assert.Equal(ExchangeCallOutcome.TimedOut, await call);
        Assert.Single(binance.Calls);
    }

    [Fact]
    public async Task A_call_that_its_caller_cancels_ends_as_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        var binance = new StandInSystem(_ =>
        {
            cancelled.Cancel();

            throw new OperationCanceledException(cancelled.Token);
        });
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => exchange.GetBalancesAsync(BinanceCalls.User, Market.Spot, TradingMode.Demo, apiKey, secretKey, cancelled.Token));
    }

    [Fact]
    public void A_reading_holds_what_was_read_when_it_succeeded_and_only_then()
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(1794562200250);

        Assert.Equal(time, new ExchangeTime(ExchangeCallOutcome.Succeeded, time).ServerTime);
        Assert.Throws<ArgumentException>(() => new ExchangeTime(ExchangeCallOutcome.Succeeded, null));
        Assert.Throws<ArgumentException>(() => new ExchangeTime(ExchangeCallOutcome.TimedOut, time));

        Assert.Empty(new ExchangeBalances(ExchangeCallOutcome.Succeeded, []).Balances);
        Assert.Throws<ArgumentException>(
            () => new ExchangeBalances(ExchangeCallOutcome.Failed, [new ExchangeBalance("USDT", 1m, 1m)]));
    }

    private static async Task<ExchangeCallOutcome> OutcomeOfAsync(Task<ExchangeTime> call) => (await call).Outcome;

    private static async Task<ExchangeCallOutcome> OutcomeOfAsync(Task<ExchangeBalances> call) => (await call).Outcome;
}
