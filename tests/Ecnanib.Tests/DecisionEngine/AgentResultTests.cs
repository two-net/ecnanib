using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Agent equity is the capital the user allocated to an agent, plus the agent's net profit or
/// loss to date. Both loss limits include unrealized profit and loss (FR-RSK-8), and the system
/// computes quantity from the agent's equity (FR-RSK-5). This is the equity that the positions,
/// the orders, the executions, and the funding of an agent add up to, as whatever asks what an
/// agent holds is told it. See "The equity of an agent" in docs/orders.md.
/// </summary>
/// <remarks>
/// The agent of these tests has a capital of 1000 USDT. The Binance of the tests executes an
/// order at the market at its price, which is 60000 until a test moves it, and takes a fee of a
/// tenth of a percent: 0.6 USDT for the 0.01 that a plan buys on Spot, and 1.2 USDT for the 0.02
/// that one buys or sells on Futures. The price that a position is valued at is that price.
/// </remarks>
public class AgentResultTests
{
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
    public async Task What_was_closed_at_the_exchange_by_no_order_of_the_agent_counts_as_closed_at_the_price_that_it_was_entered_at()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(4));

        // Its user closes the position at Binance by hand, far below its entry. No order of the
        // agent did it, so no execution of it is stored: what it lost there is not known until
        // the executions that were done by hand are read, which is not built.
        application.Binance.Price = 50000m;
        application.Binance.HoldByHand(Market.Futures, 0m);
        Assert.True(await application.ReconcileAsync(agent.Id));

        Assert.Equal(ExitReason.ClosedAtExchange, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);

        // The fee of its entry is all that is counted.
        Assert.Equal(998.8m, (await HoldsAsync(application, agent)).Equity);
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
            Guid.CreateVersion7(), agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, PositionSide.Long,
            entryPrice, quantity: 0.00700000m, stopLoss: 70000m, takeProfit: null, openedAt: at, closedAt: at.AddMinutes(5),
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
            new Fill(Guid.CreateVersion7(), stopLossId, agent.UserId, "9001", exitPrice, 0.00700000m, fee: 0.00000700m, feeAsset: "BTC", at));

        var holds = await HoldsAsync(application, agent);

        // What the position lost, and the fee that was taken in the asset at the price of its
        // execution, each to 12 decimal places: far below anything that is traded.
        var lost = Math.Round(0.00700000m * (exitPrice - entryPrice), 12);
        var fee = Math.Round(0.00000700m * exitPrice, 12);

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
            Guid.CreateVersion7(), agent.Id, agent.UserId, agent.Mode, agent.Market, agent.Symbol, PositionSide.Long,
            entryPrice: 60000m, quantity: 0.01m, stopLoss: 59000m, takeProfit: null, openedAt: at, closedAt: at.AddMinutes(5),
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
            new Fill(Guid.CreateVersion7(), stopLossId, agent.UserId, "9001", 59000m, 0.004m, fee: 0.236m, feeAsset: "USDT", at),
            new Fill(Guid.CreateVersion7(), stopLossId, agent.UserId, "9002", 58900m, 0.006m, fee: 0.3534m, feeAsset: "USDT", at));

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
