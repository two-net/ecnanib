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
/// stored. Every call reads, and costs request weight only, but for the tests of the orders,
/// which place orders in the test environment: no order is ever placed on the real exchange.
/// Two of them place orders far from the price, and take them off again. One buys the least
/// that an order can be for at the market, on the Spot market, and sells it again: that is the
/// only order that is executed, and it trades with the funds of the test environment, which are
/// none. See docs/exchange.md.
/// </para>
/// <para>
/// Those tests settle what Binance's documentation leaves open about the orders: that the test
/// environment takes a stop on both markets, on Futures with the calls of its conditional
/// orders, what Binance answers when it is asked about an order that it does not have, which
/// is what says that an order was not placed (FR-BIN-6), and that the test environment of the
/// Spot market takes an order at a price and the pair of a stop-loss and a take-profit, and
/// reports each order of a pair that it takes off under the identifier that the order carried
/// (FR-BIN-S2).
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
    public async Task The_position_of_an_account_in_a_symbol_of_the_Futures_market_is_read_with_the_credential_of_its_environment(TradingMode mode)
    {
        var (apiKey, secretKey) = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        var position = await exchange.GetFuturesPositionAsync(
            BinanceCalls.User, mode, apiKey, secretKey, TrackedSymbol, TestContext.Current.CancellationToken);

        // Binance took the signature, and what it sent reads: as no position, or as one with
        // its prices, whichever the account of the run holds. Nothing of the account is changed.
        Assert.Equal(ExchangeCallOutcome.Succeeded, position.Outcome);

        if (position.Holds)
        {
            Assert.True(position.EntryPrice > 0m);
            Assert.True(position.MarkPrice > 0m);
            Assert.True(position.LiquidationPrice is null or > 0m);
        }
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task The_funding_of_an_account_in_a_symbol_of_the_Futures_market_is_read_with_the_credential_of_its_environment(TradingMode mode)
    {
        var (apiKey, secretKey) = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();

        // The last thirty days, of the three months that Binance keeps the income of an account for.
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-30);

        var funding = await exchange.GetFundingAsync(
            BinanceCalls.User, mode, apiKey, secretKey, TrackedSymbol, from, to, TestContext.Current.CancellationToken);

        // Binance took the signature, and what it sent reads as funding, of the time that was
        // asked for, each row with a number of its own: none, where the account of the run held
        // no position in the symbol at a funding time.
        Assert.Equal(ExchangeCallOutcome.Succeeded, funding.Outcome);
        Assert.Equal(funding.Fundings.Count, funding.Fundings.Select(row => row.TransactionId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(funding.Fundings, row =>
        {
            Assert.InRange(row.At, from.AddSeconds(-1), to.AddSeconds(1));
            Assert.False(string.IsNullOrWhiteSpace(row.Asset));
            Assert.NotEqual(0m, row.Amount);

            // What a position received or paid is written at its funding time, which is a whole
            // hour, and not when Binance came to write it: what reads the funding of a position
            // that was closed goes by that. See "Funding" in docs/orders.md.
            Assert.Equal((0, 0), (row.At.Minute, row.At.Second));
        });
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

    [Fact]
    public async Task An_order_at_a_price_rests_and_is_taken_off_in_the_test_environment_of_the_Spot_market()
    {
        // In the test environment only: no test places an order with real funds.
        const TradingMode mode = TradingMode.Demo;

        var (apiKey, secretKey) = GivenFor(mode);
        using var application = OnTheNetwork();
        var data = application.Services.GetRequiredService<IExchangeMarketData>();
        var orders = application.Services.GetRequiredService<IExchangeOrders>();

        var price = await data.GetPriceAsync(Market.Spot, mode, TrackedSymbol, TestContext.Current.CancellationToken);
        var symbols = await data.GetSymbolsAsync(Market.Spot, mode, TestContext.Current.CancellationToken);
        var symbol = Assert.Single(symbols.Symbols, listed => listed.Symbol == TrackedSymbol);

        // An order that buys a tenth below the price: it rests, and buys nothing while the
        // price does not fall that far. It is for a little more than the least that an order
        // can be for.
        var at = Math.Floor(price.Price!.Value * 0.9m / symbol.PriceStep) * symbol.PriceStep;
        var quantity = Math.Max(symbol.MinQuantity, Math.Ceiling(2m * symbol.MinValue / at / symbol.QuantityStep) * symbol.QuantityStep);
        var id = Guid.CreateVersion7().ToString("N");
        var which = new ExchangeOrderRef(id, TrackedSymbol, ExchangeOrderType.Limit);

        var placed = await orders.PlaceAsync(
            BinanceCalls.User,
            Market.Spot,
            mode,
            apiKey,
            secretKey,
            new ExchangeOrderRequest(id, TrackedSymbol, ExchangeOrderSide.Buy, ExchangeOrderType.Limit, quantity, at, Closes: false),
            TestContext.Current.CancellationToken);

        try
        {
            Assert.Equal(ExchangeOrderOutcome.Answered, placed.Outcome);
            Assert.Equal((ExchangeOrderStatus.Open, 0m, false), (placed.Order!.Status, placed.Order.ExecutedQuantity, placed.Order.IsOver));

            var found = await orders.FindAsync(BinanceCalls.User, Market.Spot, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);

            Assert.Equal(ExchangeOrderOutcome.Answered, found.Outcome);
            Assert.Equal((ExchangeOrderStatus.Open, placed.Order.ExchangeOrderId), (found.Order!.Status, found.Order.ExchangeOrderId));
        }
        finally
        {
            // Taken off whatever the test found, as an entry is that still rests when the next
            // cycle starts (FR-BIN-S2), so that nothing of it rests at Binance.
            var taken = await orders.CancelAsync(BinanceCalls.User, Market.Spot, mode, apiKey, secretKey, which, CancellationToken.None);

            if (placed.Outcome == ExchangeOrderOutcome.Answered)
            {
                Assert.Equal(ExchangeOrderOutcome.Answered, taken.Outcome);
                Assert.Equal((ExchangeOrderStatus.Canceled, 0m, true), (taken.Order!.Status, taken.Order.ExecutedQuantity, taken.Order.IsOver));
            }
        }
    }

    [Fact]
    public async Task What_is_bought_on_the_Spot_market_of_the_test_environment_is_protected_by_a_pair_that_rests_and_is_sold_again()
    {
        // In the test environment only: no test places an order with real funds.
        const TradingMode mode = TradingMode.Demo;

        var (apiKey, secretKey) = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var data = application.Services.GetRequiredService<IExchangeMarketData>();
        var orders = application.Services.GetRequiredService<IExchangeOrders>();

        var price = await data.GetPriceAsync(Market.Spot, mode, TrackedSymbol, TestContext.Current.CancellationToken);
        var symbols = await data.GetSymbolsAsync(Market.Spot, mode, TestContext.Current.CancellationToken);
        var symbol = Assert.Single(symbols.Symbols, listed => listed.Symbol == TrackedSymbol);

        // The symbol takes the pair, which is what an entry in it waits for on Spot (FR-BIN-S2).
        Assert.True(symbol.PairOrders);

        // Three times the least that an order can be worth, so that what is bought can rest a
        // tenth below the price as well. The account of the test environment covers it, or the
        // entry would be skipped (FR-BIN-S3): then this test is.
        var quantity = Math.Max(symbol.MinQuantity, Math.Ceiling(3m * symbol.MinValue / price.Price!.Value / symbol.QuantityStep) * symbol.QuantityStep);
        var balances = await exchange.GetBalancesAsync(BinanceCalls.User, Market.Spot, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, balances.Outcome);
        Assert.SkipUnless(
            (balances.Balances.FirstOrDefault(balance => balance.Asset == symbol.QuoteAsset)?.Available ?? 0m) >= 1.05m * quantity * price.Price.Value,
            $"The account of the test environment holds too little {symbol.QuoteAsset} for the least entry in {TrackedSymbol}.");

        // The entry, at the market: executed at once, with its executions and their fees.
        var entryId = Guid.CreateVersion7().ToString("N");
        var bought = await orders.PlaceAsync(
            BinanceCalls.User,
            Market.Spot,
            mode,
            apiKey,
            secretKey,
            new ExchangeOrderRequest(entryId, TrackedSymbol, ExchangeOrderSide.Buy, ExchangeOrderType.Market, quantity, Price: null, Closes: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeOrderOutcome.Answered, bought.Outcome);
        Assert.Equal((ExchangeOrderStatus.Filled, quantity, true), (bought.Order!.Status, bought.Order.ExecutedQuantity, bought.Order.IsOver));
        Assert.NotEmpty(bought.Order.Fills);

        // What the account holds of it: what was bought, less a fee that Binance took in the
        // asset, down to the quantity step. An agent sells that, and no more (FR-BIN-S3).
        var held = quantity - bought.Order.Fills.Where(fill => fill.FeeAsset == symbol.BaseAsset).Sum(fill => fill.Fee);
        held = Math.Floor(held / symbol.QuantityStep) * symbol.QuantityStep;

        try
        {
            // The pair: a stop-loss a tenth below the price, and a take-profit a tenth above it.
            var stopLossId = Guid.CreateVersion7().ToString("N");
            var takeProfitId = Guid.CreateVersion7().ToString("N");
            var pairId = Guid.CreateVersion7().ToString("N");
            var stopLoss = Math.Floor(price.Price.Value * 0.9m / symbol.PriceStep) * symbol.PriceStep;
            var takeProfit = Math.Floor(price.Price.Value * 1.1m / symbol.PriceStep) * symbol.PriceStep;
            var which = new ExchangePairRef(pairId, TrackedSymbol, stopLossId, takeProfitId);

            var placed = await orders.PlacePairAsync(
                BinanceCalls.User,
                mode,
                apiKey,
                secretKey,
                new ExchangePairRequest(pairId, TrackedSymbol, held, stopLossId, stopLoss, takeProfitId, takeProfit),
                TestContext.Current.CancellationToken);

            try
            {
                // Both rest, and each is found by the identifier that it carries.
                Assert.Equal(ExchangeOrderOutcome.Answered, placed.Outcome);
                Assert.Equal((ExchangeOrderStatus.Open, 0m), (placed.StopLoss!.Status, placed.StopLoss.ExecutedQuantity));
                Assert.Equal((ExchangeOrderStatus.Open, 0m), (placed.TakeProfit!.Status, placed.TakeProfit.ExecutedQuantity));
                Assert.NotEqual(placed.StopLoss.ExchangeOrderId, placed.TakeProfit.ExchangeOrderId);

                foreach (var (id, order) in new[] { (stopLossId, placed.StopLoss), (takeProfitId, placed.TakeProfit) })
                {
                    var found = await orders.FindAsync(
                        BinanceCalls.User, Market.Spot, mode, apiKey, secretKey, new ExchangeOrderRef(id, TrackedSymbol, ExchangeOrderType.Stop),
                        TestContext.Current.CancellationToken);

                    Assert.Equal(ExchangeOrderOutcome.Answered, found.Outcome);
                    Assert.Equal((ExchangeOrderStatus.Open, order.ExchangeOrderId), (found.Order!.Status, found.Order.ExchangeOrderId));
                }
            }
            finally
            {
                // Taken off as one, whatever the test found: Binance reports both orders, each
                // under the identifier that it carried.
                var taken = await orders.CancelPairAsync(BinanceCalls.User, mode, apiKey, secretKey, which, CancellationToken.None);

                if (placed.Outcome == ExchangeOrderOutcome.Answered)
                {
                    Assert.Equal(ExchangeOrderOutcome.Answered, taken.Outcome);
                    Assert.Equal(
                        (ExchangeOrderStatus.Canceled, placed.StopLoss!.ExchangeOrderId), (taken.StopLoss!.Status, taken.StopLoss.ExchangeOrderId));
                    Assert.Equal(
                        (ExchangeOrderStatus.Canceled, placed.TakeProfit!.ExchangeOrderId), (taken.TakeProfit!.Status, taken.TakeProfit.ExchangeOrderId));
                }
            }

            // Taken off once more, no such pair rests: Binance says so in its own words.
            var again = await orders.CancelPairAsync(BinanceCalls.User, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);

            Assert.Equal(ExchangeOrderOutcome.NotFound, again.Outcome);

            // And asked about, each is the order that was taken off, with nothing executed.
            var after = await orders.FindAsync(
                BinanceCalls.User, Market.Spot, mode, apiKey, secretKey, new ExchangeOrderRef(takeProfitId, TrackedSymbol, ExchangeOrderType.Stop),
                TestContext.Current.CancellationToken);

            Assert.Equal(ExchangeOrderOutcome.Answered, after.Outcome);
            Assert.Equal((ExchangeOrderStatus.Canceled, 0m), (after.Order!.Status, after.Order.ExecutedQuantity));
        }
        finally
        {
            // What was bought is sold again, at the market, whatever the test found: the
            // account is left as it was, less the fees of the two orders.
            var sold = await orders.PlaceAsync(
                BinanceCalls.User,
                Market.Spot,
                mode,
                apiKey,
                secretKey,
                new ExchangeOrderRequest(
                    Guid.CreateVersion7().ToString("N"), TrackedSymbol, ExchangeOrderSide.Sell, ExchangeOrderType.Market, held, Price: null, Closes: true),
                CancellationToken.None);

            Assert.Equal(ExchangeOrderOutcome.Answered, sold.Outcome);
            Assert.Equal((ExchangeOrderStatus.Filled, held), (sold.Order!.Status, sold.Order.ExecutedQuantity));
        }
    }

    [Fact]
    public async Task What_is_bought_on_the_Futures_market_of_the_test_environment_is_margined_and_protected_by_two_orders_and_closed_again()
    {
        // In the test environment only: no test places an order with real funds.
        const TradingMode mode = TradingMode.Demo;
        const int leverage = 2;

        var (apiKey, secretKey) = GivenFor(mode);
        using var application = OnTheNetwork();
        var exchange = application.Services.GetRequiredService<IExchangeAdapter>();
        var data = application.Services.GetRequiredService<IExchangeMarketData>();
        var orders = application.Services.GetRequiredService<IExchangeOrders>();

        var price = await data.GetPriceAsync(Market.Futures, mode, TrackedSymbol, TestContext.Current.CancellationToken);
        var mark = await data.GetMarkPriceAsync(mode, TrackedSymbol, TestContext.Current.CancellationToken);
        var symbols = await data.GetSymbolsAsync(Market.Futures, mode, TestContext.Current.CancellationToken);
        var symbol = Assert.Single(symbols.Symbols, listed => listed.Symbol == TrackedSymbol);

        // The symbol takes a stop and a take-profit, which is what an entry in it waits for
        // on Futures (FR-BIN-F3).
        Assert.True(symbol.StopOrders);
        Assert.True(symbol.TakeProfitOrders);

        // How the account stands: read, and nothing of it changed. An agent does not start in
        // an account that is in another mode than one-way with one asset, or that holds a
        // position in the symbol (FR-BIN-F2): then this test is skipped.
        var account = await exchange.GetFuturesAccountAsync(BinanceCalls.User, mode, apiKey, secretKey, TrackedSymbol, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, account.Outcome);
        Assert.SkipWhen(
            account.HedgeMode || account.MultiAssetsMargin || account.HoldsPosition,
            $"The account of the test environment is in hedge mode or in Multi-Assets mode, or holds a position in {TrackedSymbol}.");

        // Twice the least that an order can be worth. The wallet of the test environment
        // covers its margin, or the entry would be skipped (FR-BIN-F6): then this test is.
        var quantity = Math.Max(symbol.MinQuantity, Math.Ceiling(2m * symbol.MinValue / price.Price!.Value / symbol.QuantityStep) * symbol.QuantityStep);
        var balances = await exchange.GetBalancesAsync(BinanceCalls.User, Market.Futures, mode, apiKey, secretKey, TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeCallOutcome.Succeeded, balances.Outcome);
        Assert.SkipUnless(
            (balances.Balances.FirstOrDefault(balance => balance.Asset == symbol.QuoteAsset)?.Available ?? 0m) >= 1.05m * quantity * price.Price.Value / leverage,
            $"The wallet of the test environment holds too little {symbol.QuoteAsset} for the margin of the least entry in {TrackedSymbol}.");

        // The margin of the symbol and its leverage, before the entry (FR-BIN-F2): set, and
        // set once more, which Binance answers with its word that nothing is to change. That
        // is no refusal.
        Assert.Equal(
            ExchangeOrderOutcome.Answered,
            await orders.SetIsolatedMarginAsync(BinanceCalls.User, mode, apiKey, secretKey, TrackedSymbol, leverage, TestContext.Current.CancellationToken));
        Assert.Equal(
            ExchangeOrderOutcome.Answered,
            await orders.SetIsolatedMarginAsync(BinanceCalls.User, mode, apiKey, secretKey, TrackedSymbol, leverage, TestContext.Current.CancellationToken));

        // The entry, at the market: executed at once, at a price that the answer says.
        var bought = await orders.PlaceAsync(
            BinanceCalls.User,
            Market.Futures,
            mode,
            apiKey,
            secretKey,
            new ExchangeOrderRequest(
                Guid.CreateVersion7().ToString("N"), TrackedSymbol, ExchangeOrderSide.Buy, ExchangeOrderType.Market, quantity, Price: null, Closes: false),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExchangeOrderOutcome.Answered, bought.Outcome);
        Assert.Equal((ExchangeOrderStatus.Filled, quantity, true), (bought.Order!.Status, bought.Order.ExecutedQuantity, bought.Order.IsOver));
        Assert.NotNull(bought.Order.AveragePrice);

        var stopLoss = new ExchangeOrderRef(Guid.CreateVersion7().ToString("N"), TrackedSymbol, ExchangeOrderType.Stop);
        var takeProfit = new ExchangeOrderRef(Guid.CreateVersion7().ToString("N"), TrackedSymbol, ExchangeOrderType.TakeProfit);
        var closed = false;

        try
        {
            // The account holds the position now.
            var holding = await exchange.GetFuturesAccountAsync(BinanceCalls.User, mode, apiKey, secretKey, TrackedSymbol, TestContext.Current.CancellationToken);

            Assert.Equal((ExchangeCallOutcome.Succeeded, true), (holding.Outcome, holding.HoldsPosition));

            // And says how its margin stands: what it holds, what it was entered at, the mark
            // price, and the price that Binance liquidates it at, which at a leverage of 2 is
            // below the mark price of a long position, and above nothing (FR-BIN-F5).
            var held = await exchange.GetFuturesPositionAsync(BinanceCalls.User, mode, apiKey, secretKey, TrackedSymbol, TestContext.Current.CancellationToken);

            Assert.Equal((ExchangeCallOutcome.Succeeded, quantity), (held.Outcome, held.Quantity));
            Assert.True(held.EntryPrice > 0m);
            Assert.True(held.MarkPrice > 0m);
            Assert.InRange(held.LiquidationPrice!.Value, 0.01m, held.MarkPrice!.Value);

            // The protection: a stop-loss a tenth below the mark price, and a take-profit a
            // tenth above it, each an order of its own that only reduces the position, and is
            // triggered on the mark price (FR-BIN-F3).
            var below = Math.Floor(mark.MarkPrice!.Value * 0.9m / symbol.PriceStep) * symbol.PriceStep;
            var above = Math.Floor(mark.MarkPrice.Value * 1.1m / symbol.PriceStep) * symbol.PriceStep;

            var stop = await orders.PlaceAsync(
                BinanceCalls.User,
                Market.Futures,
                mode,
                apiKey,
                secretKey,
                new ExchangeOrderRequest(stopLoss.ClientOrderId, TrackedSymbol, ExchangeOrderSide.Sell, ExchangeOrderType.Stop, quantity, below, Closes: true),
                TestContext.Current.CancellationToken);
            var profit = await orders.PlaceAsync(
                BinanceCalls.User,
                Market.Futures,
                mode,
                apiKey,
                secretKey,
                new ExchangeOrderRequest(takeProfit.ClientOrderId, TrackedSymbol, ExchangeOrderSide.Sell, ExchangeOrderType.TakeProfit, quantity, above, Closes: true),
                TestContext.Current.CancellationToken);

            // Both rest, and each is found by the identifier that it carries, where Binance
            // keeps its conditional orders.
            Assert.Equal(ExchangeOrderOutcome.Answered, stop.Outcome);
            Assert.Equal(ExchangeOrderOutcome.Answered, profit.Outcome);
            Assert.Equal((ExchangeOrderStatus.Open, 0m), (stop.Order!.Status, stop.Order.ExecutedQuantity));
            Assert.Equal((ExchangeOrderStatus.Open, 0m), (profit.Order!.Status, profit.Order.ExecutedQuantity));
            Assert.NotEqual(stop.Order.ExchangeOrderId, profit.Order.ExchangeOrderId);

            foreach (var (which, order) in new[] { (stopLoss, stop.Order), (takeProfit, profit.Order) })
            {
                var found = await orders.FindAsync(BinanceCalls.User, Market.Futures, mode, apiKey, secretKey, which, TestContext.Current.CancellationToken);

                Assert.Equal(ExchangeOrderOutcome.Answered, found.Outcome);
                Assert.Equal((ExchangeOrderStatus.Open, order.ExchangeOrderId), (found.Order!.Status, found.Order.ExchangeOrderId));
            }

            // The take-profit is taken off, and the stop-loss rests on: neither is the other's.
            var taken = await orders.CancelAsync(BinanceCalls.User, Market.Futures, mode, apiKey, secretKey, takeProfit, TestContext.Current.CancellationToken);
            var rests = await orders.FindAsync(BinanceCalls.User, Market.Futures, mode, apiKey, secretKey, stopLoss, TestContext.Current.CancellationToken);

            Assert.Equal((ExchangeOrderOutcome.Answered, ExchangeOrderStatus.Canceled), (taken.Outcome, taken.Order!.Status));
            Assert.Equal((ExchangeOrderOutcome.Answered, ExchangeOrderStatus.Open), (rests.Outcome, rests.Order!.Status));

            // The position is closed at the market, by an order that only reduces it, while
            // the stop-loss rests.
            var sold = await orders.PlaceAsync(
                BinanceCalls.User,
                Market.Futures,
                mode,
                apiKey,
                secretKey,
                new ExchangeOrderRequest(
                    Guid.CreateVersion7().ToString("N"), TrackedSymbol, ExchangeOrderSide.Sell, ExchangeOrderType.Market, quantity, Price: null, Closes: true),
                TestContext.Current.CancellationToken);

            closed = sold.Outcome == ExchangeOrderOutcome.Answered;

            Assert.Equal(ExchangeOrderOutcome.Answered, sold.Outcome);
            Assert.Equal((ExchangeOrderStatus.Filled, quantity), (sold.Order!.Status, sold.Order.ExecutedQuantity));

            // What Binance does with an order that only reduces a position once the position
            // is closed, its documentation does not say. Whatever it is, the order is known to
            // Binance still, nothing of it is executed, and it is one that can be taken off:
            // what places the orders takes it off itself, and counts on neither.
            var left = await orders.FindAsync(BinanceCalls.User, Market.Futures, mode, apiKey, secretKey, stopLoss, TestContext.Current.CancellationToken);

            Assert.Equal(ExchangeOrderOutcome.Answered, left.Outcome);
            Assert.Equal(0m, left.Order!.ExecutedQuantity);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"With its position closed, Binance has the stop-loss that only reduces as {left.Order.Status}.");

            if (!left.Order.IsOver)
            {
                var off = await orders.CancelAsync(BinanceCalls.User, Market.Futures, mode, apiKey, secretKey, stopLoss, TestContext.Current.CancellationToken);

                Assert.Equal((ExchangeOrderOutcome.Answered, ExchangeOrderStatus.Canceled), (off.Outcome, off.Order!.Status));
            }

            // The account holds nothing in the symbol again.
            var after = await exchange.GetFuturesAccountAsync(BinanceCalls.User, mode, apiKey, secretKey, TrackedSymbol, TestContext.Current.CancellationToken);

            Assert.Equal((ExchangeCallOutcome.Succeeded, false), (after.Outcome, after.HoldsPosition));
        }
        finally
        {
            // Whatever the test found, nothing of it is left: both orders are taken off, and
            // what was bought is sold again, by an order that opens nothing.
            await orders.CancelAsync(BinanceCalls.User, Market.Futures, mode, apiKey, secretKey, takeProfit, CancellationToken.None);
            await orders.CancelAsync(BinanceCalls.User, Market.Futures, mode, apiKey, secretKey, stopLoss, CancellationToken.None);

            if (!closed)
            {
                await orders.PlaceAsync(
                    BinanceCalls.User,
                    Market.Futures,
                    mode,
                    apiKey,
                    secretKey,
                    new ExchangeOrderRequest(
                        Guid.CreateVersion7().ToString("N"), TrackedSymbol, ExchangeOrderSide.Sell, ExchangeOrderType.Market, quantity, Price: null, Closes: true),
                    CancellationToken.None);
            }
        }
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
