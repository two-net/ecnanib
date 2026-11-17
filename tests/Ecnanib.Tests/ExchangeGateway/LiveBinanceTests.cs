using System.Text.Json.Nodes;
using Ecnanib.ExchangeGateway;
using Ecnanib.ExchangeGateway.Trading;
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
/// stored. Every call reads, and costs request weight only, but for one test, which places a
/// stop in the test environment, far from the price, and takes it off again: no order is ever
/// placed on the real exchange, and none that is executed. See docs/exchange.md.
/// </para>
/// <para>
/// That test settles what Binance's documentation leaves open about the orders: that the test
/// environment takes a stop on both markets, on Futures with the calls of its conditional
/// orders, and what Binance answers when it is asked about an order that it does not have,
/// which is what says that an order was not placed (FR-BIN-6).
/// </para>
/// <para>
/// They also settle what Binance's documentation leaves open: that one credential of the test
/// environment works at the addresses of both its markets, how each address answers a
/// credential that it does not know, and under which name an answer says what Binance has
/// counted against the address, which the limiter goes by (FR-BIN-4).
/// </para>
/// <para>
/// The market data needs no credential, and is asked under the same condition all the same, so
/// that no run reaches Binance that was not meant to. Those tests settle that the test
/// environment serves the calls of the market data, what they cost, and that the whole list of
/// a market's symbols arrives within the limit of a call (FR-BIN-3).
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

    // A symbol of the tracked configuration file, which both markets have in both environments.
    private const string TrackedSymbol = "BTCUSDT";

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

    [Theory]
    [MemberData(nameof(MarketsAndEnvironments))]
    public async Task The_candles_of_a_symbol_are_read_and_the_newest_closed_within_its_length(Market market, TradingMode mode)
    {
        _ = GivenFor(mode);
        using var application = OnTheNetwork();
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        foreach (var (interval, _, length) in BinanceCalls.Intervals)
        {
            var candles = await data.GetCandlesAsync(market, mode, TrackedSymbol, interval, TestContext.Current.CancellationToken);

            // Binance took the call, and what it sent reads as candles: no more than a read
            // gives, in the order of their time, each of the length that was asked for.
            Assert.Equal(ExchangeCallOutcome.Succeeded, candles.Outcome);
            Assert.InRange(candles.Candles.Count, 1, ExchangeCandles.Most);
            Assert.All(candles.Candles, candle => Assert.Equal(length, candle.ClosedAt - candle.OpenedAt));
            Assert.All(
                candles.Candles.Zip(candles.Candles.Skip(1)), pair => Assert.True(pair.First.ClosedAt <= pair.Second.OpenedAt));

            // The newest is closed, and is the latest that is: the candle after it is in
            // progress, which is what the gateway takes the last row of an answer for. In a
            // quiet market of the test environment Binance may begin a candle late.
            var now = DateTimeOffset.UtcNow;
            Assert.InRange(candles.Candles[^1].ClosedAt, now - length - TimeSpan.FromMinutes(5), now.AddMinutes(5));
        }

        // Each read cost 2 of request weight, on both markets: Binance counts the rows of the
        // Futures market, and a read asks for the most that cost 2.
        var usage = application.Services.GetRequiredService<IExchangeLimits>().Usage()
            .Single(limit => limit.Name == market.ToString() && limit.Environment == mode);
        Assert.Equal(2 * BinanceCalls.Intervals.Length, usage.Used);
        Assert.InRange(usage.Reported ?? 0, usage.Used, usage.Limit);
    }

    [Theory]
    [MemberData(nameof(MarketsAndEnvironments))]
    public async Task The_price_and_the_rules_of_a_symbol_are_read(Market market, TradingMode mode)
    {
        _ = GivenFor(mode);
        using var application = OnTheNetwork();
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var price = await data.GetPriceAsync(market, mode, TrackedSymbol, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, price.Outcome);
        Assert.True(price.Price > 0);

        // The whole list of the market arrives within the limit of a call, and the symbol is
        // among those that it has, with rules that an order can keep.
        var symbols = await data.GetSymbolsAsync(market, mode, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, symbols.Outcome);

        var symbol = Assert.Single(symbols.Symbols, listed => listed.Symbol == TrackedSymbol);
        Assert.Equal(("BTC", "USDT"), (symbol.BaseAsset, symbol.QuoteAsset));
        Assert.True(symbol.PriceStep > 0 && symbol.QuantityStep > 0 && symbol.MinValue > 0);
        Assert.True(symbol.MinQuantity > 0 && symbol.MaxQuantity > symbol.MinQuantity);
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task The_mark_price_the_funding_and_the_open_interest_of_a_symbol_of_the_Futures_market_are_read(TradingMode mode)
    {
        _ = GivenFor(mode);
        using var application = OnTheNetwork();
        var data = application.Services.GetRequiredService<IExchangeMarketData>();

        var markPrice = await data.GetMarkPriceAsync(mode, TrackedSymbol, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, markPrice.Outcome);
        Assert.True(markPrice.MarkPrice > 0);

        // Funding is paid every few hours, so the next one is ahead, and not far.
        var now = DateTimeOffset.UtcNow;
        Assert.InRange(markPrice.FundingRate!.Value, -0.1m, 0.1m);
        Assert.InRange(markPrice.NextFundingAt!.Value, now.AddMinutes(-5), now.AddHours(9));

        var openInterest = await data.GetOpenInterestAsync(mode, TrackedSymbol, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, openInterest.Outcome);
        Assert.True(openInterest.OpenInterest >= 0);
    }

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task A_stop_is_placed_found_and_taken_off_in_the_test_environment_and_an_order_that_binance_lacks_is_not_found(Market market)
    {
        // In the test environment only: no test places an order with real funds.
        const TradingMode mode = TradingMode.Demo;

        var (apiKey, secretKey) = GivenFor(mode);
        using var application = OnTheNetwork();
        var data = application.Services.GetRequiredService<IExchangeMarketData>();
        var orders = application.Services.GetRequiredService<IExchangeOrders>();

        var price = await data.GetPriceAsync(market, mode, TrackedSymbol, TestContext.Current.CancellationToken);
        var symbols = await data.GetSymbolsAsync(market, mode, TestContext.Current.CancellationToken);
        var symbol = Assert.Single(symbols.Symbols, listed => listed.Symbol == TrackedSymbol);

        // The symbol takes a stop, which is what an entry in it waits for (FR-RSK-7).
        Assert.True(symbol.StopOrders);

        // A stop that buys a tenth above the price: it rests, opens nothing while the price
        // does not rise that far, and takes no position to be there. It is for a little more
        // than the least that an order can be for.
        var trigger = Math.Floor(price.Price!.Value * 1.1m / symbol.PriceStep) * symbol.PriceStep;
        var quantity = Math.Max(symbol.MinQuantity, Math.Ceiling(2m * symbol.MinValue / trigger / symbol.QuantityStep) * symbol.QuantityStep);
        var id = Guid.CreateVersion7().ToString("N");
        var which = new ExchangeOrderRef(id, TrackedSymbol, ExchangeOrderType.Stop);

        // Before it is placed, Binance has no such order, and says so in its own words.
        var before = await orders.FindAsync(BinanceCalls.User, market, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeOrderOutcome.NotFound, before.Outcome);

        var placed = await orders.PlaceAsync(
            BinanceCalls.User,
            market,
            mode,
            apiKey,
            secretKey,
            new ExchangeOrderRequest(id, TrackedSymbol, ExchangeOrderSide.Buy, ExchangeOrderType.Stop, quantity, trigger, Closes: false),
            TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(ExchangeOrderOutcome.Answered, placed.Outcome);
            Assert.Equal((ExchangeOrderStatus.Open, 0m), (placed.Order!.Status, placed.Order.ExecutedQuantity));

            // It is found by the identifier that it carries, as the order that was placed.
            var found = await orders.FindAsync(BinanceCalls.User, market, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);

            Assert.Equal(ExchangeOrderOutcome.Answered, found.Outcome);
            Assert.Equal((ExchangeOrderStatus.Open, placed.Order.ExchangeOrderId), (found.Order!.Status, found.Order.ExchangeOrderId));
        }
        finally
        {
            // Taken off whatever the test found, so that nothing of it rests at Binance.
            var taken = await orders.CancelAsync(BinanceCalls.User, market, mode, apiKey, secretKey, which, CancellationToken.None);

            if (placed.Outcome == ExchangeOrderOutcome.Answered)
            {
                Assert.Equal(ExchangeOrderOutcome.Answered, taken.Outcome);
                Assert.Equal(ExchangeOrderStatus.Canceled, taken.Order!.Status);
            }
        }

        // Taken off once more, no such order rests.
        var again = await orders.CancelAsync(BinanceCalls.User, market, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeOrderOutcome.NotFound, again.Outcome);

        // And asked about, it is the order that was taken off, with nothing executed.
        var after = await orders.FindAsync(BinanceCalls.User, market, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeOrderOutcome.Answered, after.Outcome);
        Assert.Equal((ExchangeOrderStatus.Canceled, 0m), (after.Order!.Status, after.Order.ExecutedQuantity));
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
