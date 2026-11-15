using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Architecture;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// Ecnanib never withdraws, deposits, or transfers funds, between the Spot and the Futures wallet
/// of a user included (FR-BIN-9). The gateway has no operation that does: everything it can send
/// to Binance is a short list of calls that read, and every call it makes passes one place.
/// Binance is the only exchange behind it (FR-BIN-1).
/// </summary>
public class NoFundMovementTests
{
    // Everything that the gateway sends to Binance, whatever it is asked, in any market and any
    // environment. Binance keeps the calls that move funds in its wallet API, under sapi:
    // withdrawals, the addresses of deposits, and transfers between wallets. The one call that
    // the gateway makes there reads what a key may do.
    private static readonly string[] Reads =
    [
        "GET /api/v3/account",
        "GET /api/v3/time",
        "GET /fapi/v1/accountConfig",
        "GET /fapi/v1/time",
        "GET /fapi/v3/balance",
        "GET /sapi/v1/account/apiRestrictions",
    ];

    // The operations of the gateway, which the first test asks one by one. An operation that is
    // added is asked there too, so that what it sends is held to the list.
    private static readonly string[] Operations =
    [
        nameof(IExchangeAdapter.CheckKeyAsync),
        nameof(IExchangeAdapter.GetBalancesAsync),
        nameof(IExchangeAdapter.GetServerTimeAsync),
    ];

    [Fact]
    public async Task Whatever_the_gateway_is_asked_it_sends_binance_nothing_but_calls_that_read()
    {
        Assert.Equal(Operations, typeof(IExchangeAdapter).GetMembers().Select(member => member.Name).Order(StringComparer.Ordinal));

        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        foreach (var mode in new[] { TradingMode.Live, TradingMode.Demo })
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);
            await exchange.CheckKeyAsync(mode, apiKey, secretKey, TestContext.Current.CancellationToken);

            foreach (var market in new[] { Market.Spot, Market.Futures })
            {
                await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken);
                await exchange.GetBalancesAsync(market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);
            }
        }

        // All of the list, and nothing that is not on it.
        Assert.Equal(
            Reads,
            binance.Calls.Select(call => $"{call.Method} {call.Address.AbsolutePath}").Distinct().Order(StringComparer.Ordinal));

        // A call that reads sends nothing but its address.
        Assert.All(binance.Calls, call => Assert.Null(call.Body));

        // The wallet API is asked on the real exchange only, at the address of its Spot market.
        Assert.All(
            binance.Calls.Where(call => call.Address.AbsolutePath.StartsWith("/sapi/", StringComparison.Ordinal)),
            call => Assert.Equal(BinanceCalls.HostOf(Market.Spot, TradingMode.Live), call.Address.Host));
    }

    [Fact]
    public async Task Binance_is_the_only_exchange_and_is_reached_at_the_addresses_of_the_configuration_file_only()
    {
        var binance = BinanceCalls.ThatAnswers();
        using var application = BinanceCalls.Behind(binance);

        // One adapter is behind the common interface.
        var exchange = Assert.Single(application.Services.GetServices<IExchangeAdapter>());

        foreach (var (market, mode) in BinanceCalls.Pairs)
        {
            var (apiKey, secretKey) = BinanceCalls.CredentialOf(mode);
            await exchange.CheckKeyAsync(mode, apiKey, secretKey, TestContext.Current.CancellationToken);
            await exchange.GetServerTimeAsync(market, mode, TestContext.Current.CancellationToken);
            await exchange.GetBalancesAsync(market, mode, apiKey, secretKey, TestContext.Current.CancellationToken);
        }

        // Each of the four addresses is reached, and no other.
        Assert.Equal(
            BinanceCalls.Pairs.Select(pair => BinanceCalls.HostOf(pair.Market, pair.Mode)).Order(StringComparer.Ordinal),
            binance.Calls.Select(call => call.Address.Host).Distinct().Order(StringComparer.Ordinal));
        Assert.All(binance.Calls, call => Assert.Equal("https", call.Address.Scheme));
    }

    [Fact]
    public void Every_call_to_binance_passes_the_one_place_that_takes_only_a_call_of_the_list()
    {
        const string Gateway = "Ecnanib.ExchangeGateway";
        const string TheCall = "Ecnanib.ExchangeGateway.Adapters.Binance.BinanceCall";

        // What takes a client from the host's factory, and what sends with one. A type that
        // did either beside the call could send Binance anything.
        var users = new[] { "System.Net.Http.IHttpClientFactory::", "System.Net.Http.HttpClient::", "System.Net.Http.HttpMessageInvoker::" }
            .SelectMany(method => DependencyChecker.FindCallersOf(typeof(Program).Assembly, method))
            .Where(caller => caller.StartsWith(Gateway + ".", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        // The rule finds the call itself, so it would find another.
        Assert.NotEmpty(users);
        Assert.All(users, caller => Assert.StartsWith(TheCall, caller));
    }
}
