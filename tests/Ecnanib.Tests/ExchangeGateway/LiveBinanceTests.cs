using System.Text.Json.Nodes;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// The gateway against Binance itself. Every other test of the gateway answers in Binance's
/// place, with what its documentation says it sends. These ask Binance, so they show that it
/// takes the calls and answers as the adapter reads it, in each market and each environment
/// (FR-BIN-1, FR-BIN-2).
/// </summary>
/// <remarks>
/// <para>
/// They are skipped unless whoever runs them gives a credential for an environment, in the
/// environment of the run: <c>ECNANIB_LIVE_BINANCE_DEMO_KEY</c> and
/// <c>ECNANIB_LIVE_BINANCE_DEMO_SECRET</c> for Binance's test environment, and the same with
/// <c>LIVE</c> for the real exchange. So the suite never reaches Binance by itself, and no key is
/// stored. Every call reads, and costs request weight only. See docs/exchange.md.
/// </para>
/// <para>
/// They also settle what Binance's documentation leaves open: that one credential of the test
/// environment works at the addresses of both its markets, how each address answers a
/// credential that it does not know, and under which name an answer says what Binance has
/// counted against the address, which the limiter goes by (FR-BIN-4).
/// </para>
/// <para>
/// The variables are read here, by the tests. The application reads none.
/// </para>
/// </remarks>
public class LiveBinanceTests
{
    // A key that Binance does not know, in the form of one: sixty-four letters and digits. It is
    // sent to Binance, so it names nothing: not the application, and not who runs the tests.
    private const string UnknownKey = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ01";
    private const string UnknownSecret = "ZYXWVUTSRQPONMLKJIHGFEDCBAzyxwvutsrqponmlkjihgfedcba9876543210ZY";

    public static TheoryData<Market, TradingMode> MarketsAndEnvironments => BinanceCalls.MarketsAndEnvironments;

    public static TheoryData<TradingMode> Environments => [TradingMode.Demo, TradingMode.Live];

    [Theory]
    [MemberData(nameof(MarketsAndEnvironments))]
    public async Task The_clock_of_a_market_is_read(Market market, TradingMode mode)
    {
        _ = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        var time = await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, time.Outcome);

        // It is a time of now, in UTC: within what a clock of a machine may be off by.
        var now = DateTimeOffset.UtcNow;
        Assert.InRange(time.ServerTime!.Value, now.AddMinutes(-5), now.AddMinutes(5));
        Assert.Equal(TimeSpan.Zero, time.ServerTime.Value.Offset);

        // Binance said with its answer what it has counted against this address, under the
        // name that the limiter reads: its documentation gives the name as a pattern only.
        var usage = application.Services.GetRequiredService<IExchangeLimits>().Usage()
            .Single(limit => limit.Name == market.ToString() && limit.Environment == mode);
        Assert.Equal(1, usage.Used);
        Assert.InRange(usage.Reported ?? 0, 1, usage.Limit);
    }

    [Theory]
    [MemberData(nameof(MarketsAndEnvironments))]
    public async Task The_balances_of_a_market_are_read_with_the_credential_of_its_environment(Market market, TradingMode mode)
    {
        var (apiKey, secretKey) = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        var balances = await exchange.GetBalancesAsync(
            BinanceCalls.User, market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

        // Binance took the signature, and what it sent reads as balances. In the test
        // environment the same credential is asked at the address of each market.
        Assert.Equal(ExchangeCallOutcome.Succeeded, balances.Outcome);
        Assert.Equal(
            balances.Balances.Count, balances.Balances.Select(balance => balance.Asset).Distinct(StringComparer.Ordinal).Count());
        Assert.All(balances.Balances, balance => Assert.False(string.IsNullOrWhiteSpace(balance.Asset)));
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task Binance_takes_the_credential_and_says_what_it_may_do(TradingMode mode)
    {
        var (apiKey, secretKey) = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        var check = await exchange.CheckKeyAsync(BinanceCalls.User, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

        // A key of the real exchange that is restricted to other addresses than this machine's is
        // refused here, as a wrong one is: allow this machine's address at Binance for the run.
        Assert.Equal(KeyVerdict.Accepted, check.Verdict);

        // Only the real exchange reports whether a key can withdraw and whether it is restricted
        // to addresses.
        Assert.Equal(mode == TradingMode.Live, check.CanWithdraw is not null);
        Assert.Equal(mode == TradingMode.Live, check.IpRestricted is not null);
    }

    [Theory]
    [MemberData(nameof(MarketsAndEnvironments))]
    public async Task A_market_answers_a_credential_it_does_not_know_as_the_gateway_expects(Market market, TradingMode mode)
    {
        _ = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        var balances = await exchange.GetBalancesAsync(
            BinanceCalls.User, market, mode, UnknownKey, UnknownSecret, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.AuthenticationFailed, balances.Outcome);
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task The_check_of_a_credential_that_binance_does_not_know_is_a_refusal(TradingMode mode)
    {
        _ = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        // On the real exchange this asks its wallet API, whose answer to an unknown key is in no
        // list of Binance's errors.
        Assert.Equal(
            ExchangeKeyCheck.Refused,
            await exchange.CheckKeyAsync(BinanceCalls.User, mode, UnknownKey, UnknownSecret, TestContext.Current.CancellationToken));
    }

    /// <summary>The credential that the run was given for an environment. Without both keys, the test is skipped.</summary>
    private static (string ApiKey, string SecretKey) GivenFor(TradingMode mode)
    {
        var name = $"ECNANIB_LIVE_BINANCE_{mode.ToString().ToUpperInvariant()}";
        var apiKey = Environment.GetEnvironmentVariable($"{name}_KEY");
        var secretKey = Environment.GetEnvironmentVariable($"{name}_SECRET");

        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secretKey),
            $"Set {name}_KEY and {name}_SECRET to call Binance for real. See docs/exchange.md.");

        return (apiKey!.Trim(), secretKey!.Trim());
    }

    /// <summary>
    /// The application with the network behind the client of the gateway, and with Binance as the
    /// tracked configuration file has it: its real addresses.
    /// </summary>
    private static LoggedApplication OnTheNetwork()
    {
        // The test project copies the tracked file into its output under this name.
        var tracked = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.tracked.json")))!;

        return new LoggedApplication(
            LoggedApplication.Settings().With("Binance", tracked["Binance"]!.DeepClone()),
            host => host.ConfigureServices(services => services
                .AddHttpClient(ExchangeGatewayModule.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler())));
    }
}
