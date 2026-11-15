using System.Text;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// The gateway reaches Binance's Spot market and its USDⓈ-margined Futures market, each on the
/// real exchange and in Binance's test environment, at the four addresses of the configuration
/// file (FR-BIN-1, FR-BIN-2). A call that needs a credential is signed with the one it was given
/// for the environment it asks, and the secret key is never sent. Every call is logged with its
/// duration, its outcome, and the request weight that Binance reports (NFR-LOG-5).
/// </summary>
public class BinanceGatewayTests
{
    [Fact]
    public async Task The_clock_of_each_market_is_read_at_its_address_in_each_environment()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            Assert.Equal(
                new ExchangeTime(ExchangeCallOutcome.Succeeded, DateTimeOffset.FromUnixTimeMilliseconds(1794562200250)),
                await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken));
        }

        // The addresses are those of the configuration file. No call is signed: there is no
        // query, and no key.
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

    [Theory]
    [MemberData(nameof(BinanceCalls.MarketsAndEnvironments), MemberType = typeof(BinanceCalls))]
    public async Task The_balances_of_a_market_are_read_at_its_address_with_a_call_that_the_credential_signs(
        Market market, TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

        var balances = await exchange.GetBalancesAsync(market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, balances.Outcome);

        // One call, which reads, to the address of the market in the environment.
        var call = Assert.Single(binance.Calls);
        Assert.Equal("GET", call.Method);
        Assert.Equal(
            $"{BinanceCalls.AddressOf(market, mode)}/{(market == Market.Spot ? "api/v3/account" : "fapi/v3/balance")}",
            call.Endpoint);
        Assert.Null(call.Body);
        Assert.Equal(apiKey, call.Headers["X-MBX-APIKEY"]);

        // It says when it was made, by the application's clock, and the signature is the last of it.
        string[] signedParameters = ["recvWindow", "timestamp", "signature"];
        Assert.Equal(
            market == Market.Spot ? ["omitZeroBalances", .. signedParameters] : signedParameters,
            call.Query.Select(parameter => parameter.Name));
        Assert.Equal("5000", call.Query[^3].Value);
        Assert.Equal(BinanceCalls.Now.ToUnixTimeMilliseconds().ToString(), call.Query[^2].Value);

        // It is signed with the secret key of the credential: over the query as it was sent.
        Assert.Equal(BinanceCalls.SignatureOf(call, secretKey), call.Query[^1].Value);

        // The secret key signs, and is not sent: not in the address, and not in a header.
        Assert.DoesNotContain(secretKey, call.Address.ToString());
        Assert.DoesNotContain(apiKey, call.Address.ToString());
        Assert.All(call.Headers.Values, value => Assert.DoesNotContain(secretKey, value));
    }

    [Fact]
    public async Task Each_environment_is_asked_with_the_credential_that_was_given_for_it()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);
            await exchange.GetBalancesAsync(market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);
        }

        // The credential of the test environment reaches no address of the real exchange, and
        // the other way around: the environment of a call selects where it goes.
        Assert.Equal(4, binance.Calls.Count);
        Assert.All(binance.Calls, call =>
        {
            var mode = call.Address.Host.Contains("-live.", StringComparison.Ordinal) ? TradingMode.Live : TradingMode.Demo;
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

            Assert.Equal(apiKey, call.Headers["X-MBX-APIKEY"]);
            Assert.Equal(BinanceCalls.SignatureOf(call, secretKey), call.Query[^1].Value);
        });
    }

    [Fact]
    public async Task What_the_account_of_the_Spot_market_holds_is_read_as_its_balances()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Live);

        var balances = await exchange.GetBalancesAsync(
            Market.Spot, TradingMode.Live, apiKey, secretKey, TestContext.Current.CancellationToken);

        // All of an asset is what is free and what open orders hold, to the last digit.
        Assert.Equal(
            [
                new ExchangeBalance("BTC", Total: 4723846.89208129m, Available: 4723846.89208129m),
                new ExchangeBalance("USDT", Total: 1500.00000001m, Available: 1250.5m),
            ],
            balances.Balances);
    }

    [Fact]
    public async Task What_the_wallet_of_the_Futures_market_holds_is_read_as_its_balances()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(TradingMode.Demo);

        var balances = await exchange.GetBalancesAsync(
            Market.Futures, TradingMode.Demo, apiKey, secretKey, TestContext.Current.CancellationToken);

        // An asset that the wallet holds none of is left out. One that it owes is not.
        Assert.Equal(
            [
                new ExchangeBalance("USDT", Total: 122607.35137903m, Available: 23.72469206m),
                new ExchangeBalance("USDC", Total: -1.25m, Available: 0m),
            ],
            balances.Balances);
    }

    [Theory]
    [MemberData(nameof(BinanceCalls.MarketsAndEnvironments), MemberType = typeof(BinanceCalls))]
    public async Task A_call_is_logged_with_its_duration_its_outcome_and_its_request_weight(Market market, TradingMode mode)
    {
        var binance = BinanceCalls.ThatAnswers(answer => answer.Headers.Add("X-MBX-USED-WEIGHT-1M", "21"));
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);

        await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken);
        await exchange.GetBalancesAsync(market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

        // A call without a credential and a signed one: each is one event.
        var calls = application.Events().Where(written => written.Area == BinanceCalls.HttpArea).ToList();
        string[] paths = market == Market.Spot ? ["/api/v3/time", "/api/v3/account"] : ["/fapi/v1/time", "/fapi/v3/balance"];
        Assert.Equal(paths, calls.Select(call => call.Text("Path")));
        Assert.All(calls, call =>
        {
            Assert.Equal("Information", call.Level);
            Assert.Equal("GET", call.Text("Method"));
            Assert.Equal(BinanceCalls.HostOf(market, mode), call.Text("Host"));
            Assert.Equal("Succeeded", call.Text("Outcome"));
            Assert.Equal(200, call["StatusCode"].GetInt32());
            Assert.InRange(call["DurationMs"].GetDouble(), 0, 60_000);
            Assert.Equal("21", call["Usage"].GetProperty("X-MBX-USED-WEIGHT-1M").GetString());
        });

        // A call that went well is said once: the gateway adds nothing to it.
        Assert.DoesNotContain(application.Events(), written => written.Area == BinanceCalls.GatewayArea);
    }

    [Fact]
    public async Task Neither_key_and_no_signature_is_in_a_log_event_of_any_call()
    {
        // With everything that can be logged: the debug level, at which bodies are written too.
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance, debug: true);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);
            await exchange.GetBalancesAsync(market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);
        }

        // The calls are there, with what Binance answered.
        var log = application.LogText();
        Assert.Equal(4, application.Events().Count(written => written.Area == BinanceCalls.HttpArea && !written.Has("BodyOf")));
        Assert.Contains("122607.35137903", log);

        // Not a key, in any form, and not a signature that a secret key made.
        var keys = new[] { TradingMode.Live, TradingMode.Demo }
            .Select(BinanceCalls.CredentialOf)
            .SelectMany(credential => new[] { credential.ApiKey, credential.SecretKey });
        Assert.All(
            keys.SelectMany(key => new[] { key, Convert.ToBase64String(Encoding.UTF8.GetBytes(key)) }),
            form => Assert.DoesNotContain(form, log));
        Assert.All(binance.Calls, call => Assert.DoesNotContain(call.Query[^1].Value, log));
    }
}
