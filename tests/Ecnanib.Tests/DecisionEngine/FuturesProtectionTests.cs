using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Protection is a reduce-only stop-loss and a reduce-only take-profit resting on the exchange,
/// triggered on the mark price (FR-BIN-F3). On the Futures market the two are orders of their
/// own: the exchange ends neither for the other, so the stop-loss is placed first, the
/// take-profit beside it, and what one leaves behind when it closes the position is taken off.
/// Every open position has a stop-loss order resting on the exchange (FR-RSK-7). See "The
/// take-profit of Futures" in docs/orders.md.
/// </summary>
public class FuturesProtectionTests
{
    private const string Area = "Ecnanib.DecisionEngine.AgentOrders";

    [Fact]
    public async Task A_position_is_protected_by_two_orders_of_their_own_that_only_reduce_it_and_are_triggered_on_the_mark_price()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The stop-loss first, and the take-profit beside it, each for what the position holds.
        Assert.Equal(
            [
                ("STOP_MARKET", "SELL", 0.02m, (decimal?)59000m, true, "MARK_PRICE", "NEW"),
                ("TAKE_PROFIT_MARKET", "SELL", 0.02m, 62000m, true, "MARK_PRICE", "NEW"),
            ],
            application.Binance.Orders.Skip(1).Select(order =>
                (order.Type, order.Asked.Side, order.Asked.Quantity, order.Asked.Trigger, order.Asked.ReduceOnly, order.Asked.WorkingType!, order.Status)));

        // Two rows, each with an identifier of its own, and of no pair: Binance has no list for
        // them, and each is asked about and taken off on its own.
        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Open, 59000m), (OrderPurpose.TakeProfit, OrderStatus.Open, (decimal?)62000m)],
            orders.Skip(1).Select(order => (order.Purpose, order.Status, order.Price)));
        Assert.All(orders, order => Assert.Null(order.PairId));
        Assert.Equal(3, orders.Select(order => order.ClientOrderId).Distinct().Count());

        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_take_profit_that_was_executed_closes_the_position_and_the_stop_loss_that_it_left_behind_is_taken_off()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var settled = Assert.Single(application.Application.Reconciliations);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The mark price reaches the take-profit, and Binance executes it. It leaves the
        // stop-loss resting: nothing ends one of the two for the other there.
        application.Binance.Execute(application.Binance.TheTakeProfit());
        Assert.Equal("NEW", application.Binance.The(stop: true).Status);

        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow().AddMinutes(10));

        // The position is closed in the records, as one that took its profit, and the
        // stop-loss is taken off the exchange: left to rest, it would sell a position that the
        // agent opens later.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.TakeProfit, position.ExitReason);
        Assert.NotNull(position.ClosedAt);
        Assert.Equal("CANCELED", application.Binance.The(stop: true).Status);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Canceled), (OrderPurpose.TakeProfit, OrderStatus.Filled)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));

        // A take-profit that is executed is no failure: nobody is told, and the agent is
        // settled and enters again.
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.True(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        Assert.Equal(2, (await application.PositionsOfAsync(agent.Id)).Count);
    }

    [Fact]
    public async Task An_agent_with_an_order_that_a_closed_position_left_behind_is_not_settled_until_it_is_taken_off()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        var settled = Assert.Single(application.Application.Reconciliations);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The stop-loss is executed, and Binance does not take the take-profit off when it is asked to.
        application.Binance.Execute(application.Binance.The(stop: true));
        application.Binance.Instead = request => request.Method == HttpMethod.Delete
            ? StandInSystem.Json(System.Net.HttpStatusCode.ServiceUnavailable, string.Empty)
            : null;

        await application.SettleAsync(agent.Id);

        // The position is closed, and its take-profit rests still. The agent gets no cycle,
        // and no entry, while that is so.
        Assert.Equal(ExitReason.StopLoss, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
        Assert.Equal("NEW", application.Binance.TheTakeProfit().Status);
        Assert.False(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // The job that settles the orders takes it off with its next run, whenever it was last asked about.
        application.Binance.Instead = null;
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal("CANCELED", application.Binance.TheTakeProfit().Status);
        Assert.True(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_take_profit_that_Binance_refuses_closes_the_position_at_market_and_notifies_the_user()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The mark price has passed the take-profit by the time that the stop-loss rests:
        // Binance refuses an order that would be triggered at once.
        application.Binance.Fate = asked => asked.TakesProfit ? OrderFate.Refused : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The entry, the stop-loss, which rests, the take-profit that was refused, which is not
        // sent again, and then the stop-loss is taken off and the position closed at the market.
        Assert.Equal(
            ["POST /fapi/v1/order", "POST /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder", "DELETE /fapi/v1/algoOrder", "POST /fapi/v1/order"],
            application.Binance.OrderCalls);

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.ProtectionFailed, position.ExitReason);
        Assert.NotNull(position.ClosedAt);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Rejected),
                (OrderPurpose.Close, OrderStatus.Filled),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));

        // Its user is told which of the two could not be placed.
        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal((NotificationType.ProtectionFailure, agent.Id), (notification.Type, notification.AgentId));
        Assert.Equal(
            "The take-profit of the position in BTCUSDT (Demo) could not be placed, so the position was closed at market.",
            notification.Content);

        // The agent runs on: the position is closed, and nothing is left to settle.
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_position_whose_take_profit_is_refused_and_that_cannot_be_closed_gets_its_stop_loss_back_and_its_user_is_told()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // Binance refuses the take-profit, and the order that closes the position as well.
        application.Binance.Fate = asked => asked.TakesProfit || (asked.Type == "MARKET" && asked.ReduceOnly) ? OrderFate.Refused : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The position is open, and has a stop-loss that rests again: the take-profit never
        // leaves it without one for longer than the close takes. The take-profit is given up,
        // and the position says none.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59000m, null, null), (position.StopLoss, position.TakeProfit, position.ClosedAt));
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Rejected),
                (OrderPurpose.Close, OrderStatus.Rejected),
                (OrderPurpose.StopLoss, OrderStatus.Open),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal("NEW", application.Binance.Orders[^1].Status);

        // Its user is told, and the agent runs on: its position is protected, and it is settled.
        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.ProtectionFailure, notification.Type);
        Assert.Equal(
            "The take-profit of the position in BTCUSDT (Demo) could not be placed, and the position could not be closed at market " +
            "either. It is open with its stop-loss, and with no take-profit. Close it with \"Close position now\", or at Binance.",
            notification.Content);
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // It is not tried again with every run: the stop-loss would be taken off every time.
        var sent = application.Binance.System.Calls.Count(call => call.Method != "GET");
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());
        await application.SettleAsync(agent.Id);

        Assert.Equal(sent, application.Binance.System.Calls.Count(call => call.Method != "GET"));
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));

        // A plan that names a take-profit has one placed, beside the stop-loss that rests.
        application.Binance.Fate = null;
        await application.PlaceAsync(agent, GuardCases.Adjust with { Market = Market.Futures, StopLoss = null, TakeProfit = 62500m });

        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Open, 59000m), (OrderPurpose.TakeProfit, OrderStatus.Open, (decimal?)62500m)],
            (await application.OrdersOfAsync(agent.Id)).TakeLast(2).Select(order => (order.Purpose, order.Status, order.Price)));
    }

    [Fact]
    public async Task A_cycle_that_is_stopped_once_the_stop_loss_rests_leaves_the_take_profit_to_what_settles_the_orders()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The cycle is stopped while Binance has the entry: the next close is near, or the
        // application is shutting down.
        using var stopping = new CancellationTokenSource();
        application.Binance.Fate = asked =>
        {
            if (asked.Type == "MARKET")
            {
                stopping.Cancel();
            }

            return OrderFate.Taken;
        };

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures, stopping.Token);

        // The entry is not left without its stop-loss (NFR-REL-5), which is the one more call
        // that a cycle that is stopped has. The agent is settled: its position is protected.
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // The take-profit is owed, and the job that settles the orders places it with its next run.
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(("NEW", (decimal?)62000m), (application.Binance.TheTakeProfit().Status, application.Binance.TheTakeProfit().Asked.Trigger));
    }

    [Fact]
    public async Task A_stop_loss_that_was_taken_off_at_Binance_is_placed_again_beside_the_take_profit_that_rests()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // Its user takes the stop-loss off by hand, at Binance. The take-profit rests on.
        application.Binance.TakeOff(application.Binance.The(stop: true));

        await application.SettleAsync(agent.Id);

        // The position has a stop-loss again: a new order, with an identifier of its own. The
        // take-profit is the one that it was, and was not touched for it.
        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Open),
                (OrderPurpose.StopLoss, OrderStatus.Open),
            ],
            orders.Select(order => (order.Purpose, order.Status)));
        Assert.Equal("NEW", Assert.Single(application.Binance.Orders, order => order.Asked.ClientOrderId == orders[3].ClientOrderId).Status);
        Assert.Equal("NEW", application.Binance.TheTakeProfit().Status);
        Assert.DoesNotContain("DELETE /fapi/v1/algoOrder", application.Binance.OrderCalls);
    }

    [Fact]
    public async Task A_take_profit_that_was_taken_off_at_Binance_is_placed_again_while_the_stop_loss_rests()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        var stopLoss = application.Binance.The(stop: true);

        application.Binance.TakeOff(application.Binance.TheTakeProfit());

        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow().AddMinutes(10));

        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Open),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Open),
            ],
            orders.Select(order => (order.Purpose, order.Status)));
        Assert.Equal("NEW", stopLoss.Status);
        Assert.Equal("NEW", Assert.Single(application.Binance.Orders, order => order.Asked.ClientOrderId == orders[3].ClientOrderId).Status);
    }

    [Fact]
    public async Task A_plan_that_moves_the_take_profit_alone_leaves_the_stop_loss_resting_where_it_is()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        var stopLoss = application.Binance.The(stop: true);

        await application.PlaceAsync(agent, GuardCases.Adjust with { Market = Market.Futures, StopLoss = null, TakeProfit = 63000m });

        // The take-profit that rested is taken off, and one rests at the new price. The
        // position has its stop-loss all the while: nothing was done with it.
        Assert.Equal(
            ["POST /fapi/v1/order", "POST /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder", "DELETE /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder"],
            application.Binance.OrderCalls);
        Assert.Equal("NEW", stopLoss.Status);
        Assert.Equal(
            [("CANCELED", (decimal?)62000m), ("NEW", 63000m)],
            application.Binance.Orders.Where(order => order.TakeProfit).Select(order => (order.Status, order.Asked.Trigger)));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59000m, (decimal?)63000m, null), (position.StopLoss, position.TakeProfit, position.ClosedAt));
    }

    [Fact]
    public async Task A_take_profit_that_cannot_be_placed_at_its_new_price_goes_back_to_where_it_was()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // Binance refuses the take-profit at its new price.
        application.Binance.Fate = asked => asked.TakesProfit && asked.Trigger == 63000m ? OrderFate.Refused : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.Adjust with { Market = Market.Futures, StopLoss = null, TakeProfit = 63000m });

        // The position has its take-profit at the old price again, and is not closed. Its
        // stop-loss rested all the while.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59000m, (decimal?)62000m, null), (position.StopLoss, position.TakeProfit, position.ClosedAt));
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, null),
                (OrderPurpose.StopLoss, OrderStatus.Open, 59000m),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled, 62000m),
                (OrderPurpose.TakeProfit, OrderStatus.Rejected, 63000m),
                (OrderPurpose.TakeProfit, OrderStatus.Open, (decimal?)62000m),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Price)));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.Single(
            application.Application.Events(),
            said => said.Area == Area
                && said.Template == "The take-profit of the position of agent {AgentId} could not be placed at its new price, so it is placed at the old one again");
    }

    [Fact]
    public async Task The_take_profit_is_taken_off_before_the_stop_loss_when_a_position_is_closed()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        var orders = await application.OrdersOfAsync(agent.Id);

        Assert.Equal(PositionCloseOutcome.Closed, await application.CloseAsync(agent.Id));

        // What is left when Binance does not take one of the two off is the stop-loss.
        Assert.Equal(
            [orders[2].ClientOrderId, orders[1].ClientOrderId],
            application.Binance.System.Calls
                .Where(call => call.Method == "DELETE")
                .Select(call => call.Query.Single(parameter => parameter.Name == "clientAlgoId").Value));
        Assert.Equal(ExitReason.UserClose, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Fact]
    public async Task A_take_profit_whose_position_was_closed_meanwhile_is_not_sent_once_more()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        // The call that places the take-profit is lost, and the cycle is stopped at that
        // moment, so it does not wait to ask about it.
        using var stopping = new CancellationTokenSource();
        application.Binance.Fate = asked =>
        {
            if (!asked.TakesProfit)
            {
                return OrderFate.Taken;
            }

            stopping.Cancel();

            return OrderFate.Lost;
        };

        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures, stopping.Token);

        Assert.Equal(OrderStatus.Unknown, (await application.OrdersOfAsync(agent.Id))[2].Status);

        // The stop-loss is executed before the take-profit is asked about.
        application.Binance.Fate = null;
        application.Binance.Execute(application.Binance.The(stop: true));
        application.Clock.Set(application.Clock.GetUtcNow().AddMinutes(1));

        await application.SettleAsync(agent.Id);

        // Binance does not have the take-profit, and it can no longer arrive. An order that
        // protects is sent once more as the same order, but not for a position that is closed:
        // it would rest with nothing to protect, and act on the next position.
        Assert.Equal(ExitReason.StopLoss, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled, 1), (OrderPurpose.StopLoss, OrderStatus.Filled, 1), (OrderPurpose.TakeProfit, OrderStatus.NotPlaced, 1)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Sends)));
        Assert.Single(application.Binance.Asked, asked => asked.TakesProfit);
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
    }
}
