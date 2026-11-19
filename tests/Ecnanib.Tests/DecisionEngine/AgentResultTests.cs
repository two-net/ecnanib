using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Agent equity is the capital the user allocated to an agent, plus the agent's net profit or
/// loss to date. Both loss limits include unrealized profit and loss (FR-RSK-8), and the system
/// computes quantity from the agent's equity (FR-RSK-5). This is the equity that the positions,
/// the orders, the executions, and the funding of an agent add up to, as whatever asks what an
/// agent holds is told it. Fees and funding payments are taken from exchange data and included
/// in every profit and loss figure (FR-BIN-10). See "The equity of an agent" and "The value of
/// a fee" in docs/orders.md.
/// </summary>
/// <remarks>
/// The agent of these tests has a capital of 1000 USDT. The Binance of the tests executes an
/// order at the market at its price, which is 60000 until a test moves it, and takes a fee of a
/// tenth of a percent: 0.6 USDT for the 0.01 that a plan buys on Spot, and 1.2 USDT for the 0.02
/// that one buys or sells on Futures. The price that a position is valued at is that price.
/// </remarks>
public class AgentResultTests
{
    private const string Fees = "Ecnanib.DecisionEngine.FeeValuation";

    [Fact]
    public async Task An_agent_that_has_traded_nothing_has_the_capital_that_its_user_allocated_as_its_equity()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        var holds = await HoldsAsync(application, agent);

        // No profit and no loss to date: it has lost nothing in the day, and is at its peak.
        Assert.Equal((1000m, 1000m, 1000m, true), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity, holds.Valued));

        // No price is asked for where nothing is held: the rows say it all.
        Assert.Equal(0, application.FundingTimes.Asked);
    }

    [Fact]
    public async Task The_equity_of_an_agent_includes_what_its_open_position_would_make_at_the_price_now()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Bought 0.01 at 60000, for a fee of 0.6: at that price the position has made nothing.
        Assert.Equal(999.4m, (await HoldsAsync(application, agent)).Equity);

        // Unrealized profit, and unrealized loss: 0.01 times how far the price has moved.
        application.Binance.Price = 61000m;
        Assert.Equal(1009.4m, (await HoldsAsync(application, agent)).Equity);

        application.Binance.Price = 59000m;
        Assert.Equal(989.4m, (await HoldsAsync(application, agent)).Equity);
    }

    [Fact]
    public async Task A_short_position_gains_when_the_price_falls_and_is_valued_at_the_mark_price()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenShort);

        // The executions of an entry on Futures are read when its orders are settled.
        await application.SettleAsync(agent.Id);
        var markPricesAsked = application.FundingTimes.Asked;

        // Sold 0.02 at 60000, for a fee of 1.2.
        application.Binance.Price = 59000m;
        Assert.Equal(1018.8m, (await HoldsAsync(application, agent)).Equity);

        application.Binance.Price = 61000m;
        Assert.Equal(978.8m, (await HoldsAsync(application, agent)).Equity);

        // The mark price is what Binance values a position of Futures at, and what its
        // stop-loss is triggered on (FR-BIN-F3): it was asked for each time.
        Assert.Equal(markPricesAsked + 2, application.FundingTimes.Asked);
    }

    [Fact]
    public async Task What_a_position_made_is_what_the_order_that_closed_it_was_executed_at_less_the_fees()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Its user closes it at 61000: 10 made, less the fees of the purchase and of the sale,
        // which are 0.6 and 0.61.
        application.Binance.Price = 61000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        // The executions of the order that closed it, with their fees, are read when the orders
        // of the agent are next settled, which is within a minute: until then its fee is not counted.
        Assert.Equal(1009.4m, (await HoldsAsync(application, agent)).Equity);
        await application.SettleAsync(agent.Id);

        Assert.Equal(1008.79m, (await HoldsAsync(application, agent)).Equity);

        // It holds nothing now: where the price goes changes nothing, and none is asked for.
        application.Binance.Price = 50000m;
        application.FundingTimes.Prices = ExchangeCallOutcome.TimedOut;

        var holds = await HoldsAsync(application, agent);
        Assert.Equal((1008.79m, true), (holds.Equity, holds.Valued));
    }

    [Fact]
    public async Task A_stop_loss_that_is_executed_is_a_loss_of_the_agent()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The price reaches the stop-loss at 59000, which sells the 0.01 there: 10 lost, with
        // the fees of the purchase and of the sale, which are 0.6 and 0.59.
        application.Binance.Execute(application.Binance.The(stop: true));
        await application.SettleAsync(agent.Id);

        Assert.Equal(ExitReason.StopLoss, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
        Assert.Equal(988.81m, (await HoldsAsync(application, agent)).Equity);
    }

    [Fact]
    public async Task A_fee_that_is_taken_in_the_asset_that_was_bought_is_counted_at_the_price_of_its_execution_and_once()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // Binance takes its fee of the purchase in what was bought: 0.00001 BTC of the 0.01,
        // which is worth 0.6 USDT at the 60000 of the execution. The position holds 0.00999.
        application.Binance.FeeAsset = "BTC";
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Equal(999.4m, (await HoldsAsync(application, agent)).Equity);

        // What it holds gains with the price, and the fee is not taken from it a second time.
        application.Binance.Price = 61000m;
        Assert.Equal(1009.39m, (await HoldsAsync(application, agent)).Equity);
    }

    [Fact]
    public async Task What_a_position_received_and_paid_as_funding_is_in_the_equity_of_its_agent()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));

        // As what watches the positions stores it: the position paid 0.12, and received 0.05.
        // What it received in another asset is nothing of what its symbol is priced in.
        await StoreAsync(
            application,
            new Funding(Guid.CreateVersion7(), position.Id, agent.UserId, "7001", -0.12m, "USDT", OrdersApplication.Start),
            new Funding(Guid.CreateVersion7(), position.Id, agent.UserId, "7002", 0.05m, "USDT", OrdersApplication.Start),
            new Funding(Guid.CreateVersion7(), position.Id, agent.UserId, "7003", 3m, "BNB", OrdersApplication.Start));

        // 1000, less the fee of 1.2, less 0.07.
        Assert.Equal(998.73m, (await HoldsAsync(application, agent)).Equity);
    }

    [Fact]
    public async Task What_an_agent_did_in_Demo_is_no_part_of_its_equity_in_Live()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Binance.Price = 59000m;
        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));
        await application.SettleAsync(agent.Id);

        // In Demo it has lost 10, and the fees.
        Assert.Equal(988.81m, (await HoldsAsync(application, agent)).Equity);

        // The same agent once it is switched to Live: what it did in Demo stays Demo, and is
        // never combined with what it does in Live (FR-BIN-11).
        var holds = await HoldsAsync(application, With(agent, mode: TradingMode.Live));

        Assert.Equal((1000m, 1000m, 1000m), (holds.Equity, holds.EquityAtDayStart, holds.PeakEquity));
    }

    [Fact]
    public async Task What_was_closed_at_the_exchange_by_no_order_of_the_agent_counts_as_closed_at_the_price_when_it_was_found()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // The price falls far below its entry: the 0.02 have lost 200, which is in the equity
        // of the agent with the fee of its entry.
        application.Binance.Price = 50000m;
        Assert.Equal(798.8m, (await HoldsAsync(application, agent)).Equity);

        // Its user closes the position at Binance by hand. No order of the agent did it, so no
        // execution says what it was sold at: it counts as closed at the price when that is
        // found, which is what the equity valued it at a moment ago.
        application.Binance.HoldByHand(Market.Futures, 0m);
        Assert.True(await application.ReconcileAsync(agent.Id));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((ExitReason.ClosedAtExchange, (decimal?)-200m), (position.ExitReason, position.MadeAtExchange));

        // The loss is the agent's, and is in its loss limits: its equity is as it was, and
        // stays so wherever the price goes from here.
        Assert.Equal(798.8m, (await HoldsAsync(application, agent)).Equity);

        application.Binance.Price = 60000m;
        Assert.Equal(798.8m, (await HoldsAsync(application, agent)).Equity);
    }

    [Fact]
    public async Task What_its_user_sold_of_a_position_at_the_exchange_leaves_the_equity_of_its_agent_as_it_was()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // At 61000 the 0.02 have made 20.
        application.Binance.Price = 61000m;
        Assert.Equal(1018.8m, (await HoldsAsync(application, agent)).Equity);

        // Its user sells half of it at Binance by hand: the half counts as sold at the price
        // when that is found, which is 10 made, and the other half is valued as before. Binance
        // says the price that the position was entered at as it was: a sale changes none.
        application.Binance.HoldByHand(Market.Futures, 0.01m);
        application.Binance.EnteredAt = 60000m;
        Assert.True(await application.ReconcileAsync(agent.Id));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.01m, null, (decimal?)10m), (position.Quantity, position.ClosedAt, position.MadeAtExchange));
        Assert.Equal(1018.8m, (await HoldsAsync(application, agent)).Equity);

        // What was sold no longer moves with the price: only the half that is held does.
        application.Binance.Price = 62000m;
        Assert.Equal(1028.8m, (await HoldsAsync(application, agent)).Equity);
    }

    [Fact]
    public async Task What_left_a_position_counts_as_closed_at_its_entry_price_where_the_price_is_not_known()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // The exchange does not say the price as the records are corrected: they are corrected
        // all the same, for the exchange is the source of truth for what an agent holds.
        application.Binance.Price = 50000m;
        application.Binance.HoldByHand(Market.Futures, 0m);
        application.FundingTimes.Prices = ExchangeCallOutcome.TimedOut;
        Assert.True(await application.ReconcileAsync(agent.Id));

        // Nothing is guessed: what it made is nothing, and the position says that it was
        // corrected, so its result is no measured one.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((ExitReason.ClosedAtExchange, (decimal?)0m), (position.ExitReason, position.MadeAtExchange));

        application.FundingTimes.Prices = ExchangeCallOutcome.Succeeded;
        Assert.Equal(998.8m, (await HoldsAsync(application, agent)).Equity);
    }

    [Fact]
    public async Task A_position_that_was_added_to_at_the_exchange_keeps_what_was_closed_of_it_earlier_at_the_value_that_it_had()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        var at = application.Clock.GetUtcNow();

        // As the records are after an order of the agent closed half of the position at 59000,
        // and ended with that: 10 lost, and the position holds 0.01.
        var closeId = Guid.CreateVersion7();
        await StoreAsync(
            application,
            new Order(
                closeId, agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, cycleId: null, position.Id,
                Order.ClientOrderIdOf(closeId), exchangeOrderId: "7005", OrderPurpose.Close, OrderType.Market, OrderSide.Sell,
                price: null, stopLoss: null, takeProfit: null, quantity: 0.02m, OrderStatus.Expired,
                executedQuantity: 0.01m, averagePrice: 59000m, ExitReason.UserClose, sends: 1, fillsRecorded: true,
                createdAt: at, updatedAt: at, checkedAt: at, pairId: null, cancelAfter: null));

        await using (var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await context.Set<Position>()
                .Where(row => row.Id == position.Id)
                .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.Quantity, 0.01m), TestContext.Current.CancellationToken);
        }

        // At 60800 the half that is held has made 8: 1000, less the fee of 1.2 and the 10, with the 8.
        application.Clock.Set(at.AddMinutes(4));
        application.Binance.Price = 60800m;
        Assert.Equal(996.8m, (await HoldsAsync(application, agent)).Equity);

        // The account holds 0.02: its user bought 0.01 more by hand, at 60800, and Binance says
        // the average of both as the price that the position was entered at.
        application.Binance.EnteredAt = 60400m;
        Assert.True(await application.ReconcileAsync(agent.Id));

        position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.02m, 60400m), (position.Quantity, position.EntryPrice));

        // The half that was closed at 59000 was entered at 60000, and lost 10. Counted against
        // 60400 it would have lost 14: the 4 are kept with the position, so that it lost what it
        // lost. What was bought has made nothing yet, and the equity is as it was.
        Assert.Equal(4m, position.MadeAtExchange);
        Assert.Equal(996.8m, (await HoldsAsync(application, agent)).Equity);
    }

    [Fact]
    public async Task A_fee_that_is_taken_in_another_asset_is_counted_by_what_that_asset_traded_at_when_its_execution_was_stored()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The account pays its fees with BNB: Binance takes the tenth of a percent of the
        // purchase, which is worth 0.6 USDT, as 0.0012 BNB at the 500 that BNB trades at.
        application.Binance.FeeAsset = "BNB";
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The execution is stored as the position is opened, which is before the position has
        // its stop-loss: no price is read then, and the fee has no value yet.
        var entry = (await application.OrdersOfAsync(agent.Id))[0];
        Assert.Equal((0.0012m, "BNB", null), Row(Assert.Single(await application.FillsOfAsync(entry.Id))));
        Assert.Equal(0, application.FundingTimes.FeePricesAsked);
        Assert.Equal(1000m, (await HoldsAsync(application, agent)).Equity);

        // The job that settles the orders values it with its next run, within a minute. The
        // execution says the fee as Binance took it, and what it is worth beside it: by the
        // price of BNBUSDT, which the configuration file names as a symbol that prices a fee.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.WhileTimePassesAsync(application.Application.SettleOrdersAsync(application.Clock.GetUtcNow()));

        Assert.Equal((0.0012m, "BNB", 0.6m), Row(Assert.Single(await application.FillsOfAsync(entry.Id))));
        Assert.Equal(1, application.FundingTimes.FeePricesAsked);

        // So the fee is in the equity of the agent, as one in USDT is.
        Assert.Equal(999.4m, (await HoldsAsync(application, agent)).Equity);

        // It is valued once: where BNB goes from here changes nothing of what the fee was worth.
        application.Binance.FeeAssetPrice = 900m;
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.WhileTimePassesAsync(application.Application.SettleOrdersAsync(application.Clock.GetUtcNow()));

        Assert.Equal(999.4m, (await HoldsAsync(application, agent)).Equity);
        Assert.Equal(1, application.FundingTimes.FeePricesAsked);

        static (decimal Fee, string FeeAsset, decimal? FeeValue) Row(Fill fill) => (fill.Fee, fill.FeeAsset, fill.FeeValue);
    }

    [Fact]
    public async Task A_fee_whose_price_could_not_be_read_is_valued_by_a_later_run_of_what_settles_the_orders()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        application.Binance.FeeAsset = "BNB";
        application.Binance.ExecutedAt = application.Clock.GetUtcNow();

        // The exchange does not say what BNB trades at as the executions of the entry are stored.
        application.FundingTimes.FeePrices = ExchangeCallOutcome.TimedOut;
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);

        var entry = (await application.OrdersOfAsync(agent.Id))[0];
        Assert.Equal((0.0024m, "BNB", null), Row(Assert.Single(await application.FillsOfAsync(entry.Id))));

        // The execution is stored as Binance said it, and its fee is in no figure yet. It is
        // said, with the symbol that was not priced, and never with an amount.
        Assert.Equal(1000m, (await HoldsAsync(application, agent)).Equity);

        var said = Assert.Single(application.Application.Events(), written => written.Area == Fees && written.Level == "Warning");
        Assert.Equal(
            "A fee of agent {AgentId} in {Symbol} is not valued yet: the exchange did not give the price of {FeeSymbol} ({ExchangeCallOutcome})",
            said.Template);
        Assert.Equal(("BTCUSDT", "BNBUSDT", "TimedOut"), (said.Text("Symbol"), said.Text("FeeSymbol"), said.Text("ExchangeCallOutcome")));

        // The job that settles the orders values it with its next run, and says nothing more
        // while the price is still not read.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.WhileTimePassesAsync(application.Application.SettleOrdersAsync(application.Clock.GetUtcNow()));

        Assert.Null(Assert.Single(await application.FillsOfAsync(entry.Id)).FeeValue);
        Assert.Single(application.Application.Events(), written => written.Area == Fees && written.Level == "Warning");

        // Once the exchange says the price, the fee is worth what BNB trades at then: 1.2.
        application.FundingTimes.FeePrices = ExchangeCallOutcome.Succeeded;
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.WhileTimePassesAsync(application.Application.SettleOrdersAsync(application.Clock.GetUtcNow()));

        Assert.Equal((0.0024m, "BNB", 1.2m), Row(Assert.Single(await application.FillsOfAsync(entry.Id))));
        Assert.Equal(998.8m, (await HoldsAsync(application, agent)).Equity);

        static (decimal Fee, string FeeAsset, decimal? FeeValue) Row(Fill fill) => (fill.Fee, fill.FeeAsset, fill.FeeValue);
    }

    [Fact]
    public async Task A_fee_in_an_asset_that_the_file_names_no_symbol_for_is_in_no_figure_and_is_said_once()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        application.Binance.FeeAsset = "DOGE";
        application.Binance.ExecutedAt = application.Clock.GetUtcNow();

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);

        // Nothing says what the asset is worth: the file lists BNBUSDT, and no DOGEUSDT. The
        // fee has no value, and no price is asked for.
        var entry = (await application.OrdersOfAsync(agent.Id))[0];
        Assert.Null(Assert.Single(await application.FillsOfAsync(entry.Id)).FeeValue);
        Assert.Equal(0, application.FundingTimes.FeePricesAsked);

        // The equity of the agent is known all the same, without the fee: an agent is not held
        // back for a fee.
        var holds = await HoldsAsync(application, agent);
        Assert.Equal((1000m, true), (holds.Equity, holds.Valued));

        // It is said once, as the execution is stored, and not with every run that asks again.
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));
        await application.WhileTimePassesAsync(application.Application.SettleOrdersAsync(application.Clock.GetUtcNow()));

        var said = Assert.Single(application.Application.Events(), written => written.Area == Fees && written.Level == "Warning");
        Assert.Equal(
            "A fee of agent {AgentId} in {Symbol} was taken in {FeeAsset}, which the configuration file names no symbol for: it has no " +
            "value, and is in no profit and loss figure",
            said.Template);
        Assert.Equal((agent.Id.ToString(), "BTCUSDT", "DOGE"), (said.Text("AgentId"), said.Text("Symbol"), said.Text("FeeAsset")));
    }

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task An_equity_whose_open_position_cannot_be_valued_says_that_it_is_not_known(Market market)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: market);
        await application.PlaceAsync(agent, market == Market.Spot ? GuardCases.OpenLong : GuardCases.OpenLongOnFutures);
        await application.SettleAsync(agent.Id);
        application.Binance.Price = 50000m;

        // The exchange does not say the price.
        application.FundingTimes.Prices = ExchangeCallOutcome.TimedOut;

        var holds = await HoldsAsync(application, agent);

        // It is said, and not guessed: the equity is without what the position would make, and
        // nothing is decided on it. See LossLimitHaltTests for what is not halted.
        Assert.False(holds.Valued);
        Assert.Equal(market == Market.Spot ? 999.4m : 998.8m, holds.Equity);
        Assert.NotNull(holds.Position);

        var said = Assert.Single(
            application.Application.Events(), written => written.Area == "Ecnanib.DecisionEngine.AgentResults" && written.Level == "Warning");
        Assert.Equal(
            "The position of agent {AgentId} in {Symbol} could not be valued, so the equity of the agent is not known: the exchange " +
            "did not give its price ({ExchangeCallOutcome})",
            said.Template);
        Assert.Equal(("BTCUSDT", "TimedOut"), (said.Text("Symbol"), said.Text("ExchangeCallOutcome")));

        // Once the exchange says it again, the equity is known again.
        application.FundingTimes.Prices = ExchangeCallOutcome.Succeeded;
        Assert.True((await HoldsAsync(application, agent)).Valued);
    }

    [Fact]
    public async Task Average_prices_with_many_decimal_places_are_worked_out_with()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // An entry that was executed at two prices has an average price that does not end: it
        // is stored with as many decimal places as a number can have. So is that of the order
        // that closed the position. Their product has more places than a number can be read with.
        var entryPrice = 500000.01m / 7m;
        var exitPrice = 490000.02m / 7m;
        var at = OrdersApplication.Start;

        var position = new Position(
            Guid.CreateVersion7(), agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, agent.QuoteAsset, agent.Timeframe,
            PositionSide.Long, entryPrice, quantity: 0.00700000m, stopLoss: 70000m, takeProfit: null, openedAt: at, closedAt: at.AddMinutes(5),
            ExitReason.StopLoss, alertedAt: null, fundingDueAt: null);
        var stopLossId = Guid.CreateVersion7();
        var stopLoss = new Order(
            stopLossId, agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, cycleId: null, position.Id,
            Order.ClientOrderIdOf(stopLossId), exchangeOrderId: "7002", OrderPurpose.StopLoss, OrderType.Stop, OrderSide.Sell,
            price: 70000m, stopLoss: null, takeProfit: null, quantity: 0.00700000m, OrderStatus.Filled,
            executedQuantity: 0.00700000m, averagePrice: exitPrice, exitReason: null, sends: 1, fillsRecorded: true,
            createdAt: at, updatedAt: at.AddMinutes(5), checkedAt: at.AddMinutes(5), pairId: null, cancelAfter: null);

        await StoreAsync(application, position);
        await StoreAsync(
            application,
            stopLoss,
            new Fill(
                Guid.CreateVersion7(), stopLossId, agent.UserId, "9001", exitPrice, 0.00700000m, fee: 0.00000700m, feeAsset: "BTC", at,
                feeValue: Math.Round(0.00000700m * exitPrice, 12, MidpointRounding.AwayFromZero)));

        var holds = await HoldsAsync(application, agent);

        // What the position lost, and the fee that was taken in the asset at the price of its
        // execution, each to 12 decimal places: far below anything that is traded.
        var lost = Math.Round(0.00700000m * (exitPrice - entryPrice), 12, MidpointRounding.AwayFromZero);
        var fee = Math.Round(0.00000700m * exitPrice, 12, MidpointRounding.AwayFromZero);

        Assert.Equal(1000m + lost - fee, holds.Equity);
        Assert.InRange(holds.Equity, 989.5m, 989.6m);
    }

    [Fact]
    public async Task An_order_of_which_the_exchange_said_no_average_price_is_counted_by_its_executions_once_they_are_stored()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var at = OrdersApplication.Start;

        // A position that its stop-loss closed, of which the exchange said how much was
        // executed, and no average price.
        var position = new Position(
            Guid.CreateVersion7(), agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, agent.QuoteAsset, agent.Timeframe,
            PositionSide.Long, entryPrice: 60000m, quantity: 0.01m, stopLoss: 59000m, takeProfit: null, openedAt: at, closedAt: at.AddMinutes(5),
            ExitReason.StopLoss, alertedAt: null, fundingDueAt: null);
        var stopLossId = Guid.CreateVersion7();
        var stopLoss = new Order(
            stopLossId, agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, cycleId: null, position.Id,
            Order.ClientOrderIdOf(stopLossId), exchangeOrderId: "7002", OrderPurpose.StopLoss, OrderType.Stop, OrderSide.Sell,
            price: 59000m, stopLoss: null, takeProfit: null, quantity: 0.01m, OrderStatus.Filled,
            executedQuantity: 0.01m, averagePrice: null, exitReason: null, sends: 1, fillsRecorded: false,
            createdAt: at, updatedAt: at.AddMinutes(5), checkedAt: at.AddMinutes(5), pairId: null, cancelAfter: null);

        await StoreAsync(application, position);
        await StoreAsync(application, stopLoss);

        // Until its executions are stored, what the position lost is not known, and nothing
        // is decided on a guess.
        var holds = await HoldsAsync(application, agent);
        Assert.False(holds.Valued);

        // They are read when the orders of the agent are settled: 0.004 at 59000, and 0.006 at
        // 58900, each for a fee in USDT.
        await StoreAsync(
            application,
            new Fill(Guid.CreateVersion7(), stopLossId, agent.UserId, "9001", 59000m, 0.004m, fee: 0.236m, feeAsset: "USDT", at, feeValue: 0.236m),
            new Fill(Guid.CreateVersion7(), stopLossId, agent.UserId, "9002", 58900m, 0.006m, fee: 0.3534m, feeAsset: "USDT", at, feeValue: 0.3534m));

        holds = await HoldsAsync(application, agent);

        // 4 and 6.6 lost, and the two fees.
        Assert.True(holds.Valued);
        Assert.Equal(1000m - 10.6m - 0.5894m, holds.Equity);
    }

    /// <summary>What an agent holds, as a cycle of it and the Risk Guard are told it.</summary>
    internal static Task<AgentHoldings> HoldsAsync(OrdersApplication application, Agent agent) =>
        Assert.Single(application.Application.Holdings).OfAsync(agent, TestContext.Current.CancellationToken);

    /// <summary>An agent as it is stored, with another mode or another capital.</summary>
    internal static Agent With(Agent agent, TradingMode? mode = null, decimal? capital = null) => new(
        agent.Id,
        agent.UserId,
        agent.Name,
        mode ?? agent.Mode,
        agent.Market,
        agent.Symbol,
        agent.QuoteAsset,
        agent.Timeframe,
        capital ?? agent.AllocatedCapital,
        agent.RiskPerTradePercent,
        agent.MinRewardToRisk,
        agent.LeverageCap,
        agent.LiquidationBuffer,
        agent.DailyLossPercent,
        agent.DrawdownPercent,
        agent.MaxEntryDistancePercent,
        agent.State,
        agent.StateCause,
        agent.StateChangedAt,
        agent.CreatedAt);

    private static async Task StoreAsync(OrdersApplication application, params object[] rows)
    {
        await using var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        context.AddRange(rows);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
