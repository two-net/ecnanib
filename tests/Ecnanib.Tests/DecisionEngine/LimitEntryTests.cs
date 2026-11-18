using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Entries are market or limit orders. An unfilled limit entry is cancelled at the next cycle
/// (FR-BIN-S2). An entry at a price rests on the exchange, so what settles the orders asks about
/// it every minute, and what it bought gets its stop-loss and its take-profit as soon as that is
/// seen (FR-RSK-7). A Spot agent sells only the quantity it bought (FR-BIN-S3). See "An entry at
/// a price" in docs/orders.md.
/// </summary>
public class LimitEntryTests
{
    private const string Area = "Ecnanib.DecisionEngine.AgentOrders";

    private const string TakenOff = "The entry of agent {AgentId} in {Symbol} that still rests is taken off the exchange: {EntryTakenOffReason}";

    /// <summary>
    /// A plan to open a long position on Spot at 59900, below the price of 60000: it risks 10
    /// over a distance of 900, so the order comes to 0.011, worth 658.9.
    /// </summary>
    private static readonly GuardCase AtAPrice = GuardCases.OpenLong with { EntryPrice = 59900m };

    [Fact]
    public async Task An_entry_at_a_price_rests_on_the_exchange_and_opens_no_position_while_it_does()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var cycle = application.CycleOf(agent);

        await application.PlaceAsync(cycle, AtAPrice);

        // One order, at the price of the plan, for the quantity that the Risk Guard computed.
        var asked = Assert.Single(application.Binance.Asked);
        Assert.Equal((false, "BUY", 0.011m, (decimal?)59900m), (asked.Stop, asked.Side, asked.Quantity, asked.Limit));
        Assert.Equal("NEW", Assert.Single(application.Binance.Orders).Status);

        // Its row says that it rests, with the protection of the position that it would open,
        // and until when it rests: the next close of the candles of the agent, which are of
        // one hour.
        var entry = Assert.Single(await application.OrdersOfAsync(agent.Id));
        Assert.Equal(
            (OrderPurpose.Entry, OrderType.Limit, OrderSide.Buy, OrderStatus.Open, 59900m, 59000m, 62000m, null),
            (entry.Purpose, entry.Type, entry.Side, entry.Status, entry.Price, entry.StopLoss, entry.TakeProfit, entry.PositionId));
        Assert.Equal(cycle.ClosedAt.AddHours(1), entry.CancelAfter);

        // The agent holds no position, and an entry that has not filled.
        Assert.Empty(await application.PositionsOfAsync(agent.Id));

        var holds = await Assert.Single(application.Application.Holdings).OfAsync(agent, TestContext.Current.CancellationToken);
        Assert.Null(holds.Position);
        Assert.Equal([new OpenOrder(OpenOrderKind.Entry, OrderSide.Buy, 0.011m, 59900m)], holds.Orders);

        // It is settled: an entry that rests does not cost the agent its next cycle, which is
        // what takes the entry off.
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Theory]
    [InlineData("USDT", 0.011)]
    [InlineData("BTC", 0.01098)]
    public async Task What_settles_the_orders_finds_the_entry_executed_and_protects_what_it_bought(string feeAsset, decimal held)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        application.Binance.FeeAsset = feeAsset;
        await application.PlaceAsync(agent, AtAPrice);

        // The price comes down to the entry, and Binance executes it. Nothing tells the service.
        application.Binance.Execute(application.Binance.The(stop: false));
        Assert.Empty(await application.PositionsOfAsync(agent.Id));

        // The job that settles the orders asks about an entry that rests with every run.
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        // The position is open at the price of the entry, for what was bought less a fee that
        // Binance took in the asset, down to the quantity step, and the pair sells that, and
        // no more (FR-BIN-S3).
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(
            (PositionSide.Long, 59900m, held, 59000m, (decimal?)62000m, null),
            (position.Side, position.EntryPrice, position.Quantity, position.StopLoss, position.TakeProfit, position.ClosedAt));

        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, 0.011m),
                (OrderPurpose.StopLoss, OrderStatus.Open, held),
                (OrderPurpose.TakeProfit, OrderStatus.Open, held),
            ],
            orders.Select(order => (order.Purpose, order.Status, order.Quantity)));
        Assert.Equal(held, application.Binance.The(stop: true).Asked.Quantity);
        Assert.Equal(held, application.Binance.TheTakeProfit().Asked.Quantity);

        // The executions were read, for the answer about an order that rested does not have them.
        Assert.Contains("GET /api/v3/myTrades", application.Binance.OrderCalls);
        Assert.Equal(feeAsset, Assert.Single(await application.FillsOfAsync(orders[0].Id)).FeeAsset);
    }

    [Fact]
    public async Task An_entry_that_still_rests_when_the_next_candle_has_closed_is_taken_off_the_exchange()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var cycle = application.CycleOf(agent);
        await application.PlaceAsync(cycle, AtAPrice);

        // Before that it rests on: what settles the orders asks about it, and leaves it.
        application.Clock.Set(cycle.ClosedAt.AddMinutes(59));
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal("NEW", Assert.Single(application.Binance.Orders).Status);
        Assert.Equal(["POST /api/v3/order", "GET /api/v3/order"], application.Binance.OrderCalls);

        // The next cycle of the agent starts at the close, and settles the orders of the agent
        // before its team decides: the entry is taken off, so the team decides on an agent
        // that holds nothing (FR-BIN-S2).
        application.Clock.Set(cycle.ClosedAt.AddHours(1));
        await application.SettleAsync(agent.Id);

        Assert.Equal("CANCELED", Assert.Single(application.Binance.Orders).Status);
        Assert.Equal("DELETE /api/v3/order", application.Binance.OrderCalls[^1]);

        var entry = Assert.Single(await application.OrdersOfAsync(agent.Id));
        Assert.Equal((OrderStatus.Canceled, 0m, null), (entry.Status, entry.ExecutedQuantity, entry.PositionId));
        Assert.Empty(await application.PositionsOfAsync(agent.Id));
        Assert.Equal("TimeIsUp", Assert.Single(application.Application.Events(), said => said.Area == Area && said.Template == TakenOff).Text("EntryTakenOffReason"));

        // The plan of that cycle is placed: nothing of the old entry stands in its way.
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Single(await application.PositionsOfAsync(agent.Id));
    }

    [Fact]
    public async Task An_entry_whose_time_is_up_is_taken_off_also_when_no_cycle_of_its_agent_starts()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var cycle = application.CycleOf(agent);
        await application.PlaceAsync(cycle, AtAPrice);

        // The cycle at the next close does not start: it came too late, the candle was not
        // there, or the service was down. The job that settles the orders takes the entry off
        // all the same, so that it is not executed a day later at prices of the day before.
        application.Clock.Set(cycle.ClosedAt.AddHours(1).AddSeconds(20));
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal("CANCELED", Assert.Single(application.Binance.Orders).Status);
        Assert.Equal(OrderStatus.Canceled, Assert.Single(await application.OrdersOfAsync(agent.Id)).Status);
    }

    [Fact]
    public async Task A_part_that_is_executed_is_protected_at_once_and_the_rest_of_the_entry_is_taken_off()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, AtAPrice);

        // Binance executes a part of the entry, and the rest still rests.
        application.Binance.Execute(application.Binance.The(stop: false), 0.004m);

        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        // What was bought would have no stop-loss until the rest is executed, or the next
        // candle closes. So the rest is taken off, and the part is a position with its pair.
        Assert.Equal("CANCELED", application.Binance.The(stop: false).Status);
        Assert.Equal("PartlyExecuted", Assert.Single(application.Application.Events(), said => said.Template == TakenOff).Text("EntryTakenOffReason"));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.004m, 59900m, null), (position.Quantity, position.EntryPrice, position.ClosedAt));
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Canceled, 0.011m, 0.004m),
                (OrderPurpose.StopLoss, OrderStatus.Open, 0.004m, 0m),
                (OrderPurpose.TakeProfit, OrderStatus.Open, 0.004m, 0m),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Quantity, order.ExecutedQuantity)));
    }

    [Fact]
    public async Task A_part_that_no_order_can_be_for_is_left_to_grow_and_opens_no_position_when_the_entry_is_over()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var cycle = application.CycleOf(agent);
        await application.PlaceAsync(cycle, AtAPrice);

        // 0.00005 is worth less than 5, which is the least that an order of the symbol can be
        // worth: nothing could protect it, or sell it.
        application.Binance.Execute(application.Binance.The(stop: false), 0.00005m);

        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal("PARTIALLY_FILLED", application.Binance.The(stop: false).Status);
        Assert.Equal(OrderStatus.PartiallyFilled, Assert.Single(await application.OrdersOfAsync(agent.Id)).Status);

        // When its time is up, the entry is taken off. What it bought is no position: an agent
        // that held it could neither protect it nor close it, and would wait for its user for good.
        application.Clock.Set(cycle.ClosedAt.AddHours(1));
        await application.SettleAsync(agent.Id);

        var entry = Assert.Single(await application.OrdersOfAsync(agent.Id));
        Assert.Equal((OrderStatus.Canceled, 0.00005m, null), (entry.Status, entry.ExecutedQuantity, entry.PositionId));
        Assert.Empty(await application.PositionsOfAsync(agent.Id));
        Assert.Single(
            application.Application.Events(),
            said => said.Level == "Warning"
                && said.Template == "The entry of agent {AgentId} was executed as the order {ExchangeOrderId} for less than an order can be for, so it opens no position");

        // The agent is settled, and runs on. Its execution is stored all the same.
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.Equal(0.00005m, Assert.Single(await application.FillsOfAsync(entry.Id)).Quantity);
    }

    [Theory]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.Stopped)]
    public async Task An_entry_of_an_agent_that_no_longer_opens_positions_is_taken_off_the_exchange(AgentState state)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, AtAPrice);

        // Its user pauses the agent, or stops it and keeps what it holds: it gets no cycle that
        // would take the entry off, and an entry that is executed after that would open a
        // position for an agent that opens none.
        await using (var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlAsync(
                $"UPDATE agents SET state = {state.ToString()} WHERE id = {agent.Id}", TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal("CANCELED", Assert.Single(application.Binance.Orders).Status);
        Assert.Equal(OrderStatus.Canceled, Assert.Single(await application.OrdersOfAsync(agent.Id)).Status);
        Assert.Equal("NotRunning", Assert.Single(application.Application.Events(), said => said.Template == TakenOff).Text("EntryTakenOffReason"));
    }

    [Fact]
    public async Task A_close_for_the_user_of_an_agent_takes_an_entry_that_rests_off_and_finds_nothing_open()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, AtAPrice);

        // An agent has a position, as far as its user is asked, from the moment that its entry
        // is written: "Close position now" is there for an entry that rests.
        var outcome = await application.CloseAsync(agent.Id);

        // The entry is taken off, so that it opens nothing after the close, and nothing is sold.
        Assert.Equal(PositionCloseOutcome.NothingOpen, outcome);
        Assert.Equal("CANCELED", Assert.Single(application.Binance.Orders).Status);
        Assert.Equal(OrderStatus.Canceled, Assert.Single(await application.OrdersOfAsync(agent.Id)).Status);
        Assert.Equal("UserClose", Assert.Single(application.Application.Events(), said => said.Template == TakenOff).Text("EntryTakenOffReason"));
    }

    [Fact]
    public async Task A_close_for_the_user_of_an_agent_sells_what_an_entry_that_rests_has_bought()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, AtAPrice);

        // A part of the entry is executed, which the job has not seen yet.
        application.Binance.Execute(application.Binance.The(stop: false), 0.004m);

        var outcome = await application.CloseAsync(agent.Id);

        // A close settles first: the rest of the entry is taken off, and what it bought is a
        // position with its pair, as it would be a minute later. Then the pair is taken off,
        // and the position is sold at the market: what the entry bought, and nothing else
        // that the account holds (FR-BIN-S3).
        Assert.Equal(PositionCloseOutcome.Closed, outcome);
        Assert.Equal(
            [(false, "BUY", 0.011m), (true, "SELL", 0.004m), (false, "SELL", 0.004m)],
            application.Binance.Asked.Select(asked => (asked.Stop, asked.Side, asked.Quantity)));
        Assert.All(application.Binance.Orders.Where(order => order.Asked.Stop), order => Assert.Equal("CANCELED", order.Status));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((ExitReason.UserClose, 0.004m), (position.ExitReason, position.Quantity));
    }

    [Fact]
    public async Task An_entry_at_a_price_that_the_market_has_reached_is_executed_at_once_and_protected()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The price of the plan is above the price of the symbol: Binance executes the order
        // at the price of the symbol, which is the better one. The order risks 10 over a
        // distance of 1100, which comes to 0.009.
        await application.PlaceAsync(agent, GuardCases.OpenLong with { EntryPrice = 60100m });

        Assert.Equal(["POST /api/v3/order", "POST /api/v3/orderList/oco"], application.Binance.OrderCalls);

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((60000m, 0.009m, null), (position.EntryPrice, position.Quantity, position.ClosedAt));
        Assert.Equal(
            [(OrderPurpose.Entry, OrderType.Limit, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderType.Stop, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderType.Stop, OrderStatus.Open)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Type, order.Status)));
    }

    [Fact]
    public async Task An_entry_at_a_price_that_Binance_took_before_the_call_timed_out_is_found_resting_and_is_not_sent_again()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        application.Binance.Fate = _ => OrderFate.TakenUnsaid;

        await application.PlaceAsync(agent, AtAPrice);

        // Asked about by its identifier, it rests: it was sent once (FR-BIN-6).
        var entry = Assert.Single(await application.OrdersOfAsync(agent.Id));
        Assert.Equal((OrderStatus.Open, 1), (entry.Status, entry.Sends));
        Assert.Equal([entry.ClientOrderId], application.Binance.AskedAbout);
        Assert.Single(application.Binance.Asked);
    }

    [Fact]
    public async Task An_agent_with_an_entry_that_rests_is_given_no_second_entry()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, AtAPrice);

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // One entry of an agent is with the exchange at a time: an agent holds one position.
        Assert.Single(application.Binance.Asked);
        Assert.Equal(
            "Holding",
            Assert.Single(
                application.Application.Events(),
                said => said.Template == "No entry was placed for agent {AgentId} for the close at {ClosedAt}: {NoEntryReason}").Text("NoEntryReason"));
    }
}
