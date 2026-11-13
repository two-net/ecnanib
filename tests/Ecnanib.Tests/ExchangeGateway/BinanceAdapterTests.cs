using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// A Binance credential is validated with read-only calls (FR-KEY-3): the gateway asks Binance
/// what the credential may do, and says whether it can withdraw, whether it is restricted to
/// addresses, and which markets it may trade on (FR-KEY-4, FR-KEY-5). The real exchange reports
/// the permissions of a key, and its test environment does not. The calls are signed with the
/// secret key, which is never sent, and neither key is in a log event (FR-KEY-8).
/// </summary>
public class BinanceAdapterTests
{
    private const string ApiKey = "binance-api-0123456789-abcdefghij-Lm3N";
    private const string SecretKey = "binance-sealed-0123456789-abcdefghij-Zz9Y";

    private const string HttpArea = "Ecnanib.Platform.Adapters.Http.OutboundCallLogger";

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 30, 0, 250, TimeSpan.Zero);

    // A key as a careful user makes it: it trades on Spot, cannot withdraw, and works from listed addresses only.
    private const string Restrictions =
        """
        { "ipRestrict": true, "createTime": 1623840271000, "enableReading": true, "enableWithdrawals": false,
          "enableInternalTransfer": false, "enableMargin": false, "enableFutures": false, "permitsUniversalTransfer": false,
          "enableVanillaOptions": false, "enableSpotAndMarginTrading": true, "enablePortfolioMarginTrading": false }
        """;

    private const string CanTrade = """{ "canTrade": true, "canWithdraw": true, "canDeposit": true }""";

    private const string WrongKey = """{ "code": -2015, "msg": "Invalid API-key, IP, or permissions for action." }""";

    [Fact]
    public async Task The_real_exchange_is_asked_once_for_the_permissions_of_the_key_with_a_signed_call()
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, Restrictions));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        var check = await exchange.CheckKeyAsync(TradingMode.Live, ApiKey, SecretKey, TestContext.Current.CancellationToken);

        Assert.Equal(
            new ExchangeKeyCheck(KeyVerdict.Accepted, SpotTrading: true, FuturesTrading: false, CanWithdraw: false, IpRestricted: true),
            check);

        // One call, which reads, to the address of the Spot market on the real exchange.
        var call = Assert.Single(binance.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal("https://spot-live.example/sapi/v1/account/apiRestrictions", call.Endpoint);
        Assert.Equal(ApiKey, call.Headers["X-MBX-APIKEY"]);

        // It says when it was made, by the application's clock, and is signed with the secret key.
        Assert.Equal(["recvWindow", "timestamp", "signature"], call.Query.Select(parameter => parameter.Name));
        Assert.Equal("5000", call.Query[0].Value);
        Assert.Equal(Now.ToUnixTimeMilliseconds().ToString(), call.Query[1].Value);
        Assert.Equal(SignatureOf(call), call.Query[2].Value);

        // The secret key signs, and is not sent: not in the address, and not in a header.
        Assert.DoesNotContain(SecretKey, call.Address.ToString());
        Assert.DoesNotContain(ApiKey, call.Address.ToString());
        Assert.All(call.Headers.Values, value => Assert.DoesNotContain(SecretKey, value));
    }

    [Fact]
    public async Task A_key_that_can_withdraw_or_works_from_any_address_is_reported_as_one()
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(
            HttpStatusCode.OK,
            """{ "ipRestrict": false, "enableWithdrawals": true, "enableSpotAndMarginTrading": true, "enableFutures": true }"""));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        var check = await exchange.CheckKeyAsync(TradingMode.Live, ApiKey, SecretKey, TestContext.Current.CancellationToken);

        Assert.Equal(
            new ExchangeKeyCheck(KeyVerdict.Accepted, SpotTrading: true, FuturesTrading: true, CanWithdraw: true, IpRestricted: false),
            check);
    }

    [Theory]
    [InlineData("""{ "ipRestrict": true, "enableSpotAndMarginTrading": true, "enableFutures": true }""")]
    [InlineData("""{ "ipRestrict": true, "enableWithdrawals": "no", "enableSpotAndMarginTrading": true }""")]
    [InlineData("""[ { "enableWithdrawals": false } ]""")]
    [InlineData("<html>Maintenance</html>")]
    public async Task An_answer_that_does_not_say_whether_the_key_can_withdraw_is_no_verdict(string answer)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, answer));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        // A key is never taken for one that cannot withdraw because the answer did not say (NFR-LEG-2).
        Assert.Equal(
            ExchangeKeyCheck.Unreachable,
            await exchange.CheckKeyAsync(TradingMode.Live, ApiKey, SecretKey, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -2008, "msg": "Invalid Api-Key ID." }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -2014, "msg": "API-key format invalid." }""")]
    [InlineData(HttpStatusCode.Unauthorized, WrongKey)]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -1022, "msg": "Signature for this request is not valid." }""")]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": -1002, "msg": "You are not authorized to execute this request." }""")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    public async Task What_binance_answers_a_wrong_credential_with_is_a_refusal(HttpStatusCode status, string body)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(status, body));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        Assert.Equal(
            ExchangeKeyCheck.Refused,
            await exchange.CheckKeyAsync(TradingMode.Live, ApiKey, SecretKey, TestContext.Current.CancellationToken));
    }

    [Theory]

    // The clock of the service is off, which is not the user's key being wrong.
    [InlineData(HttpStatusCode.BadRequest, """{ "code": -1021, "msg": "Timestamp for this request is outside of the recvWindow." }""")]

    // A limit on requests, and the block that follows when it is ignored.
    [InlineData(HttpStatusCode.TooManyRequests, """{ "code": -1003, "msg": "Too much request weight used." }""")]
    [InlineData((HttpStatusCode)418, """{ "code": -1003, "msg": "Way too much request weight used; IP banned." }""")]

    // The firewall in front of the exchange, and a fault of the exchange.
    [InlineData(HttpStatusCode.Forbidden, "<html>Forbidden</html>")]
    [InlineData(HttpStatusCode.InternalServerError, """{ "code": -1000, "msg": "An unknown error occurred." }""")]
    [InlineData(HttpStatusCode.BadGateway, "")]
    public async Task Any_other_answer_says_nothing_about_the_credential(HttpStatusCode status, string body)
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(status, body));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        Assert.Equal(
            ExchangeKeyCheck.Unreachable,
            await exchange.CheckKeyAsync(TradingMode.Live, ApiKey, SecretKey, TestContext.Current.CancellationToken));
        Assert.Equal(
            ExchangeKeyCheck.Unreachable,
            await exchange.CheckKeyAsync(TradingMode.Demo, ApiKey, SecretKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_exchange_that_does_not_answer_says_nothing_about_the_credential_and_the_check_does_not_fail()
    {
        Exception failure = new HttpRequestException("The connection was refused.");
        var binance = new StandInSystem(_ => throw failure);
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        Assert.Equal(
            ExchangeKeyCheck.Unreachable,
            await exchange.CheckKeyAsync(TradingMode.Live, ApiKey, SecretKey, TestContext.Current.CancellationToken));

        // One that takes too long ends the same way: the call is given up, and nobody cancelled.
        failure = new TaskCanceledException("Binance took too long.");
        Assert.Equal(
            ExchangeKeyCheck.Unreachable,
            await exchange.CheckKeyAsync(TradingMode.Demo, ApiKey, SecretKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_test_environment_reports_no_permissions_so_the_account_of_each_market_is_read()
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, CanTrade));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        var check = await exchange.CheckKeyAsync(TradingMode.Demo, ApiKey, SecretKey, TestContext.Current.CancellationToken);

        // Whether the key can withdraw, and whether it is restricted to addresses, is not
        // reported there. What an account says of withdrawals is about the account.
        Assert.Equal(
            new ExchangeKeyCheck(KeyVerdict.Accepted, SpotTrading: true, FuturesTrading: true, CanWithdraw: null, IpRestricted: null),
            check);

        Assert.Equal(
            ["GET https://spot-demo.example/api/v3/account", "GET https://futures-demo.example/fapi/v1/accountConfig"],
            binance.Calls.Select(call => $"{call.Method} {call.Endpoint}"));
        Assert.Equal(
            ["omitZeroBalances", "recvWindow", "timestamp", "signature"], binance.Calls[0].Query.Select(parameter => parameter.Name));
        Assert.Equal(["recvWindow", "timestamp", "signature"], binance.Calls[1].Query.Select(parameter => parameter.Name));
        Assert.All(binance.Calls, call =>
        {
            Assert.Equal(ApiKey, call.Headers["X-MBX-APIKEY"]);
            Assert.Equal(SignatureOf(call), call.Query[^1].Value);
            Assert.DoesNotContain(SecretKey, call.Address.ToString());
        });
    }

    [Theory]
    [InlineData("spot", true, false)]
    [InlineData("futures", false, true)]
    public async Task A_market_of_the_test_environment_that_refuses_the_credential_is_one_it_may_not_trade_on(
        string taking, bool spotTrading, bool futuresTrading)
    {
        var binance = new StandInSystem(call => call.RequestUri!.Host.StartsWith(taking, StringComparison.Ordinal)
            ? StandInSystem.Json(HttpStatusCode.OK, CanTrade)
            : StandInSystem.Json(HttpStatusCode.Unauthorized, WrongKey));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        Assert.Equal(
            new ExchangeKeyCheck(KeyVerdict.Accepted, spotTrading, futuresTrading),
            await exchange.CheckKeyAsync(TradingMode.Demo, ApiKey, SecretKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_account_of_the_test_environment_that_may_not_trade_gives_no_permission_for_its_market()
    {
        var binance = new StandInSystem(call => StandInSystem.Json(
            HttpStatusCode.OK,
            call.RequestUri!.Host.StartsWith("spot", StringComparison.Ordinal) ? """{ "canTrade": false }""" : CanTrade));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        Assert.Equal(
            new ExchangeKeyCheck(KeyVerdict.Accepted, SpotTrading: false, FuturesTrading: true),
            await exchange.CheckKeyAsync(TradingMode.Demo, ApiKey, SecretKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_credential_that_both_markets_of_the_test_environment_refuse_is_refused()
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.Unauthorized, WrongKey));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        Assert.Equal(
            ExchangeKeyCheck.Refused,
            await exchange.CheckKeyAsync(TradingMode.Demo, ApiKey, SecretKey, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("spot", HttpStatusCode.OK)]
    [InlineData("spot", HttpStatusCode.Unauthorized)]
    [InlineData("futures", HttpStatusCode.OK)]
    [InlineData("futures", HttpStatusCode.Unauthorized)]
    public async Task A_market_of_the_test_environment_that_does_not_answer_leaves_no_verdict(string silent, HttpStatusCode other)
    {
        // Whatever the other market says: the permission of the silent one is not known.
        var binance = new StandInSystem(call => call.RequestUri!.Host.StartsWith(silent, StringComparison.Ordinal)
            ? StandInSystem.Status(HttpStatusCode.ServiceUnavailable)
            : StandInSystem.Json(other, other == HttpStatusCode.OK ? CanTrade : WrongKey));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        Assert.Equal(
            ExchangeKeyCheck.Unreachable,
            await exchange.CheckKeyAsync(TradingMode.Demo, ApiKey, SecretKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_check_that_its_caller_cancels_ends_as_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        var binance = new StandInSystem(_ =>
        {
            cancelled.Cancel();

            throw new OperationCanceledException(cancelled.Token);
        });
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => exchange.CheckKeyAsync(TradingMode.Live, ApiKey, SecretKey, cancelled.Token));
    }

    [Fact]
    public async Task The_clock_of_each_market_is_read_at_its_address_in_each_environment()
    {
        var binance = new StandInSystem(_ => StandInSystem.Json(HttpStatusCode.OK, """{ "serverTime": 1794562200250 }"""));
        using var application = Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        foreach (var (market, mode) in new[]
        {
            (Market.Spot, TradingMode.Live), (Market.Spot, TradingMode.Demo),
            (Market.Futures, TradingMode.Live), (Market.Futures, TradingMode.Demo),
        })
        {
            Assert.Equal(
                DateTimeOffset.FromUnixTimeMilliseconds(1794562200250),
                await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken));
        }

        // The addresses are those of the configuration file (FR-BIN-2). No call is signed.
        Assert.Equal(
            [
                "GET https://spot-live.example/api/v3/time",
                "GET https://spot-demo.example/api/v3/time",
                "GET https://futures-live.example/fapi/v1/time",
                "GET https://futures-demo.example/fapi/v1/time",
            ],
            binance.Calls.Select(call => $"{call.Method} {call.Address}"));
        Assert.All(binance.Calls, call => Assert.False(call.Headers.ContainsKey("X-MBX-APIKEY")));
    }

    [Fact]
    public async Task The_calls_are_logged_with_their_request_weight_and_neither_key_is_in_a_log_event()
    {
        // With everything that can be logged: the debug level, at which bodies are written too.
        var binance = new StandInSystem(_ =>
        {
            var answer = StandInSystem.Json(HttpStatusCode.OK, Restrictions);
            answer.Headers.Add("X-MBX-USED-WEIGHT-1M", "7");

            return answer;
        });
        using var application = Behind(binance, debug: true);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        await exchange.CheckKeyAsync(TradingMode.Live, ApiKey, SecretKey, TestContext.Current.CancellationToken);

        var call = Assert.Single(application.Events(), written => written.Area == HttpArea && !written.Has("BodyOf"));
        Assert.Equal("spot-live.example", call.Text("Host"));
        Assert.Equal("/sapi/v1/account/apiRestrictions", call.Text("Path"));
        Assert.Equal("Succeeded", call.Text("Outcome"));
        Assert.Equal("7", call["Usage"].GetProperty("X-MBX-USED-WEIGHT-1M").GetString());
        Assert.True(call.Has("DurationMs"));

        // Not a key, in any form, and not the signature that the secret key made.
        var log = application.LogText();
        Assert.All(
            new[] { ApiKey, SecretKey }.SelectMany(key => new[] { key, Convert.ToBase64String(Encoding.UTF8.GetBytes(key)) }),
            form => Assert.DoesNotContain(form, log));
        Assert.DoesNotContain(binance.Calls[0].Query[^1].Value, log);
    }

    private static LoggedApplication Behind(StandInSystem binance, bool debug = false) =>
        binance.Behind(
            ExchangeGatewayModule.HttpClientName,
            debug ? LoggedApplication.Settings().With("Logging:MinimumLevel", "Debug") : null,
            services => services.AddSingleton<TimeProvider>(new TestClock(Now)));

    /// <summary>
    /// What Binance works out to see that a call was signed with the secret key: the HMAC-SHA256
    /// of the query as it was sent, without the signature, in hexadecimal.
    /// </summary>
    private static string SignatureOf(ReceivedCall call)
    {
        var query = call.Address.Query.TrimStart('?');
        var signed = query[..query.LastIndexOf("&signature=", StringComparison.Ordinal)];

        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(signed)));
    }
}
