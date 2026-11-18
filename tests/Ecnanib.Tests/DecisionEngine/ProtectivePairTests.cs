using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway.Trading;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Protection is a stop-loss and take-profit pair resting on the exchange, where filling one
/// cancels the other (FR-BIN-S2). A Spot agent sells only the quantity it bought (FR-BIN-S3). The
/// pair is two orders that Binance holds as one list: they are sent, sent once more, and taken
/// off together, and never one of them alone (FR-BIN-6). See "The pair of the Spot market" in
/// docs/orders.md.
/// </summary>
public class ProtectivePairTests
{
    private const string Area = "Ecnanib.DecisionEngine.AgentOrders";

    [Fact]
    public async Task The_pair_is_placed_with_one_call_for_what_was_bought_at_the_prices_of_the_plan()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The entry, and then one call for both orders that protect what it bought.
        Assert.Equal(["POST /api/v3/order", "POST /api/v3/orderList/oco"], application.Binance.OrderCalls);

        var (stopLoss, takeProfit) = (application.Binance.The(stop: true), application.Binance.TheTakeProfit());
        Assert.Equal(("NEW", "SELL", 0.01m, (decimal?)59000m), (stopLoss.Status, stopLoss.Asked.Side, stopLoss.Asked.Quantity, stopLoss.Asked.Trigger));
        Assert.Equal(("NEW", "SELL", 0.01m, (decimal?)62000m), (takeProfit.Status, takeProfit.Asked.Side, takeProfit.Asked.Quantity, takeProfit.Asked.Trigger));

        // Two rows, each with the identifier that its order carries, and both with the id of
        // the pair, which is the identifier that the pair carries at Binance.
        var orders = (await application.OrdersOfAsync(agent.Id)).Skip(1).ToList();
        Assert.Equal(
            [
                (OrderPurpose.StopLoss, OrderType.Stop, OrderStatus.Open, 59000m, 0.01m, stopLoss.Asked.ClientOrderId, $"{stopLoss.Id}"),
                (OrderPurpose.TakeProfit, OrderType.Stop, OrderStatus.Open, (decimal?)62000m, 0.01m, takeProfit.Asked.ClientOrderId, $"{takeProfit.Id}"),
            ],
            orders.Select(order => (order.Purpose, order.Type, order.Status, order.Price, order.Quantity, order.ClientOrderId, order.ExchangeOrderId!)));
        Assert.NotNull(orders[0].PairId);
        Assert.Equal(orders[0].PairId, orders[1].PairId);
        Assert.Equal(stopLoss.Asked.PairId, Order.PairIdOf(orders[0].PairId!.Value));

        // What the agent holds says both, for both rest: the next cycle of the agent decides on them.
        var holds = await Assert.Single(application.Application.Holdings).OfAsync(agent, TestContext.Current.CancellationToken);
        Assert.Equal(new PositionContext(PositionSide.Long, 60000m, 0.01m, 59000m, TakeProfit: 62000m), holds.Position);
        Assert.Equal(
            [new OpenOrder(OpenOrderKind.StopLoss, OrderSide.Sell, 0.01m, 59000m), new OpenOrder(OpenOrderKind.TakeProfit, OrderSide.Sell, 0.01m, 62000m)],
            holds.Orders);
    }

    [Fact]
    public async Task A_take_profit_that_was_executed_closes_the_position_in_the_records_and_its_stop_loss_has_ended()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The price reaches the take-profit, and Binance executes it and ends the stop-loss.
        // Nothing tells the service.
        application.Binance.Execute(application.Binance.TheTakeProfit());
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);

        // The next time that the orders of the agent are settled, both are asked about.
        await application.SettleAsync(agent.Id);

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.TakeProfit, position.ExitReason);
        Assert.NotNull(position.ClosedAt);

        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Expired), (OrderPurpose.TakeProfit, OrderStatus.Filled)],
            orders.Select(order => (order.Purpose, order.Status)));

        // The executions of the take-profit are stored with it.
        var fill = Assert.Single(await application.FillsOfAsync(orders[2].Id));
        Assert.True(orders[2].FillsRecorded);
        Assert.Equal((62000m, 0.01m), (fill.Price, fill.Quantity));

        // Nothing is placed for a position that is closed, the agent is settled, and nobody
        // was told anything: a take-profit that is executed is no failure.
        await application.SettleAsync(agent.Id);

        Assert.Equal(2, application.Binance.Asked.Count);
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_stop_loss_of_a_pair_that_was_executed_closes_the_position_and_its_take_profit_has_ended()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        application.Binance.Execute(application.Binance.The(stop: true));

        await application.SettleAsync(agent.Id);

        Assert.Equal(ExitReason.StopLoss, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Filled), (OrderPurpose.TakeProfit, OrderStatus.Expired)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(2, application.Binance.Asked.Count);
    }

    [Fact]
    public async Task An_order_of_a_pair_that_was_asked_about_before_the_other_was_triggered_is_asked_about_again()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The stop-loss is asked about, and rests. Then the price reaches the take-profit,
        // before the take-profit is asked about.
        var asked = 0;
        application.Binance.Instead = request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v3/order" && ++asked == 2)
            {
                application.Binance.Execute(application.Binance.TheTakeProfit());
            }

            return null;
        };

        await application.SettleAsync(agent.Id);

        // The stop-loss is not left as one that rests beside a position that is closed: Binance
        // ended it when the take-profit was triggered, which the second question finds.
        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal([orders[1].ClientOrderId, orders[2].ClientOrderId, orders[1].ClientOrderId], application.Binance.AskedAbout);
        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Expired), (OrderPurpose.TakeProfit, OrderStatus.Filled)],
            orders.Skip(1).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(ExitReason.TakeProfit, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Fact]
    public async Task A_pair_that_Binance_took_before_the_call_timed_out_is_found_resting_and_is_not_sent_again()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        application.Binance.Fate = asked => asked.Stop ? OrderFate.TakenUnsaid : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // One entry, and one pair, of which both orders were asked about and found resting.
        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(["POST /api/v3/order", "POST /api/v3/orderList/oco", "GET /api/v3/order", "GET /api/v3/order"], application.Binance.OrderCalls);
        Assert.Equal([orders[1].ClientOrderId, orders[2].ClientOrderId], application.Binance.AskedAbout);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled, 1), (OrderPurpose.StopLoss, OrderStatus.Open, 1), (OrderPurpose.TakeProfit, OrderStatus.Open, 1)],
            orders.Select(order => (order.Purpose, order.Status, order.Sends)));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_pair_that_Binance_does_not_have_is_asked_about_before_it_is_sent_once_more_as_the_same_pair()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The first call that places the pair is lost. The second one is taken.
        var pairs = 0;
        application.Binance.Fate = asked => asked.Stop && ++pairs == 1 ? OrderFate.Lost : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The pair was sent, both of its orders were asked about, and only then it was sent
        // again: as the same pair, with the three identifiers that it had, so that Binance
        // would refuse it if it had the first one after all.
        Assert.Equal(
            ["POST /api/v3/order", "POST /api/v3/orderList/oco", "GET /api/v3/order", "GET /api/v3/order", "POST /api/v3/orderList/oco"],
            application.Binance.OrderCalls);

        var sent = application.Binance.Asked.Where(asked => asked.Stop).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0], sent[1]);

        // Both rest, as the one pair of the position, and nobody was told anything.
        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Open, 2), (OrderPurpose.TakeProfit, OrderStatus.Open, 2)],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status, order.Sends)));
        Assert.Equal(3, application.Binance.Orders.Count);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_pair_that_arrived_after_all_is_found_when_Binance_refuses_the_one_that_was_sent_once_more()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // Binance takes the pair, and the answer never comes. Asked about its two orders, it
        // says that it has neither: they arrive after that.
        application.Binance.Fate = asked => asked.Stop ? OrderFate.TakenUnsaid : OrderFate.Taken;

        var asked = 0;
        application.Binance.Instead = request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v3/order" && ++asked <= 2
                ? StandInSystem.Json(HttpStatusCode.BadRequest, """{ "code": -2013, "msg": "Order does not exist." }""")
                : null;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The pair that was sent once more was refused, for its identifiers are taken. That is
        // not believed before both orders were asked about once more: they are there, and rest.
        Assert.Equal(
            [
                "POST /api/v3/order", "POST /api/v3/orderList/oco", "GET /api/v3/order", "GET /api/v3/order",
                "POST /api/v3/orderList/oco", "GET /api/v3/order", "GET /api/v3/order",
            ],
            application.Binance.OrderCalls);
        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Open, 2), (OrderPurpose.TakeProfit, OrderStatus.Open, 2)],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status, order.Sends)));

        // Binance has one pair, and the position is not closed over a refusal that was none.
        Assert.Equal(3, application.Binance.Orders.Count);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_pair_that_was_cut_off_is_sent_once_more_as_a_pair_and_never_as_one_order_alone()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var settled = Assert.Single(application.Application.Reconciliations);

        // The call that places the pair is lost, and the cycle is stopped at that moment, so
        // it does not wait to ask: both rows say that what became of the pair is not known.
        using var stopping = new CancellationTokenSource();
        application.Binance.Fate = asked =>
        {
            if (!asked.Stop)
            {
                return OrderFate.Taken;
            }

            stopping.Cancel();

            return OrderFate.Lost;
        };

        await application.PlaceAsync(agent, GuardCases.OpenLong, stopping.Token);

        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Unknown), (OrderPurpose.TakeProfit, OrderStatus.Unknown)],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status)));
        Assert.False(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // Asked at once, Binance has neither order. That says nothing yet: they could still arrive.
        application.Binance.Fate = null;
        await application.Orders.SettleAsync(agent.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, application.Binance.Asked.Count);

        // Once they can no longer arrive, the pair is sent once more: both orders with one
        // call. A stop-loss that was sent alone would rest outside its pair, where nothing
        // that takes the pair off would find it.
        application.Clock.Set(application.Clock.GetUtcNow() + ExchangeOrderTimes.FinalAfter);
        await application.Orders.SettleAsync(agent.Id, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["POST /api/v3/order", "POST /api/v3/orderList/oco", "POST /api/v3/orderList/oco"],
            application.Binance.OrderCalls.Where(call => call.StartsWith("POST", StringComparison.Ordinal)));
        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Open, 2), (OrderPurpose.TakeProfit, OrderStatus.Open, 2)],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status, order.Sends)));
        Assert.Same(application.Binance.TheTakeProfit(), application.Binance.The(stop: true).Sibling);
        Assert.True(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_pair_that_is_not_placed_at_the_second_try_closes_the_position_at_market_and_notifies_the_user()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // Every call that places a pair is lost.
        application.Binance.Fate = asked => asked.Stop ? OrderFate.Lost : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Two pairs, each sent once more after Binance said that it has neither of its orders,
        // and then the order that closes the position. No order of a pair was ever sent alone.
        Assert.Equal(
            ["POST /api/v3/order", "POST /api/v3/orderList/oco", "POST /api/v3/orderList/oco", "POST /api/v3/orderList/oco", "POST /api/v3/orderList/oco", "POST /api/v3/order"],
            application.Binance.OrderCalls.Where(call => call.StartsWith("POST", StringComparison.Ordinal)));
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, 1),
                (OrderPurpose.StopLoss, OrderStatus.NotPlaced, 2),
                (OrderPurpose.TakeProfit, OrderStatus.NotPlaced, 2),
                (OrderPurpose.StopLoss, OrderStatus.NotPlaced, 2),
                (OrderPurpose.TakeProfit, OrderStatus.NotPlaced, 2),
                (OrderPurpose.Close, OrderStatus.Filled, 1),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Sends)));

        Assert.Equal(ExitReason.ProtectionFailed, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.ProtectionFailure, notification.Type);
        Assert.Equal(
            "The stop-loss of the position in BTCUSDT (Demo) could not be placed, so the position was closed at market.",
            notification.Content);
    }

    [Fact]
    public async Task A_pair_whose_stop_loss_the_price_has_passed_is_refused_and_the_position_is_closed_at_market()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The price falls below the stop-loss of the plan as the entry is executed: Binance
        // takes a pair that sells only while the price is between its two triggers.
        application.Binance.Fate = asked =>
        {
            if (asked.Side == "BUY")
            {
                application.Binance.Price = 58900m;
            }

            return OrderFate.Taken;
        };

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Rejected),
                (OrderPurpose.TakeProfit, OrderStatus.Rejected),
                (OrderPurpose.Close, OrderStatus.Filled),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(ExitReason.ProtectionFailed, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_plan_to_adjust_protection_takes_the_pair_off_and_places_one_at_the_new_prices()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The stop-loss moves up, which reduces the risk, and the take-profit moves as well.
        await application.PlaceAsync(agent, GuardCases.Adjust with { TakeProfit = 63000m });

        // The pair that rested is taken off with one call, and one rests at the new prices.
        Assert.Equal(
            ["POST /api/v3/order", "POST /api/v3/orderList/oco", "DELETE /api/v3/orderList", "POST /api/v3/orderList/oco"],
            application.Binance.OrderCalls);
        Assert.Equal(
            [("CANCELED", (decimal?)59000m), ("CANCELED", 62000m), ("NEW", 59800m), ("NEW", 63000m)],
            application.Binance.Orders.Where(order => order.Asked.Stop).Select(order => (order.Status, order.Asked.Trigger)));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59800m, (decimal?)63000m, null), (position.StopLoss, position.TakeProfit, position.ClosedAt));

        // What the agent holds says the stop-loss and the take-profit that rest.
        var holds = await Assert.Single(application.Application.Holdings).OfAsync(agent, TestContext.Current.CancellationToken);
        Assert.Equal(new PositionContext(PositionSide.Long, 60000m, 0.01m, 59800m, TakeProfit: 63000m), holds.Position);
        Assert.Equal(
            [new OpenOrder(OpenOrderKind.StopLoss, OrderSide.Sell, 0.01m, 59800m), new OpenOrder(OpenOrderKind.TakeProfit, OrderSide.Sell, 0.01m, 63000m)],
            holds.Orders);
    }

    [Fact]
    public async Task A_plan_that_moves_the_take_profit_alone_places_the_pair_anew_and_goes_back_to_the_old_one_when_Binance_refuses_it()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The plan keeps the stop-loss where it is, and names a take-profit that the price has
        // passed already: Binance refuses the pair there.
        var moved = GuardCases.Adjust with { Position = GuardCases.Long with { StopLoss = 59000m }, StopLoss = 59000m, TakeProfit = 59900m };

        await application.PlaceAsync(agent, moved);

        // The pair is back at the prices that it had, and the position is not closed: a
        // take-profit that cannot rest is no reason to give up a position that is protected.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59000m, (decimal?)62000m, null), (position.StopLoss, position.TakeProfit, position.ClosedAt));
        Assert.Equal(
            [
                (OrderPurpose.StopLoss, OrderStatus.Canceled, 59000m),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled, 62000m),
                (OrderPurpose.StopLoss, OrderStatus.Rejected, 59000m),
                (OrderPurpose.TakeProfit, OrderStatus.Rejected, 59900m),
                (OrderPurpose.StopLoss, OrderStatus.Open, 59000m),
                (OrderPurpose.TakeProfit, OrderStatus.Open, (decimal?)62000m),
            ],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status, order.Price)));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_close_of_a_position_whose_take_profit_was_executed_meanwhile_sells_nothing()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The take-profit is executed while the team decides to close.
        application.Binance.Execute(application.Binance.TheTakeProfit());

        await application.PlaceAsync(agent, GuardCases.Close);

        // Binance has no pair to take off, and says what became of each of its orders: the
        // position is closed already, and no order is placed that would sell what the account
        // holds beside it (FR-BIN-S3).
        Assert.Equal(
            ["POST /api/v3/order", "POST /api/v3/orderList/oco", "DELETE /api/v3/orderList", "GET /api/v3/order", "GET /api/v3/order"],
            application.Binance.OrderCalls.TakeWhile(call => !call.EndsWith("Trades", StringComparison.Ordinal)));
        Assert.Equal(2, application.Binance.Asked.Count);
        Assert.Equal(ExitReason.TakeProfit, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Fact]
    public async Task A_pair_that_Binance_does_not_take_off_stays_and_the_position_is_not_closed_over_it()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Binance refuses the call that takes the pair off, with an error that says nothing of
        // its orders.
        application.Binance.Instead = request => request.Method == HttpMethod.Delete
            ? StandInSystem.Json(HttpStatusCode.BadRequest, """{ "code": -1102, "msg": "Mandatory parameter was not sent, was empty/null, or malformed." }""")
            : null;

        var outcome = await application.CloseAsync(agent.Id);

        // Both orders were asked about, as a close settles first, and once more after the
        // refusal, and rest: the position is open as it was, with its pair, and nothing was
        // sold beside a stop-loss that could still sell the same.
        var protection = (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => order.ClientOrderId).ToList();

        Assert.Equal(PositionCloseOutcome.Failed, outcome);
        Assert.Equal([.. protection, .. protection], application.Binance.AskedAbout);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(2, application.Binance.Asked.Count);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
    }

    [Fact]
    public async Task An_answer_that_Binance_lacks_one_order_of_a_pair_says_nothing_while_it_has_the_other_resting()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Asked about the stop-loss, Binance says once that it has no such order, although the
        // pair rests: it holds the two as one list, and ends neither while the other rests.
        var asked = 0;
        application.Binance.Instead = request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v3/order" && ++asked == 1
                ? StandInSystem.Json(HttpStatusCode.BadRequest, """{ "code": -2013, "msg": "Order does not exist." }""")
                : null;

        await application.SettleAsync(agent.Id);

        // The stop-loss is not written off, and no second pair is placed beside the first. A
        // second pair would sell the same quantity once more when the first has sold it, out
        // of what else the account holds (FR-BIN-S3).
        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(2, application.Binance.Asked.Count);
        Assert.Equal(["NEW", "NEW"], application.Binance.Orders.Where(order => order.Asked.Stop).Select(order => order.Status));
    }

    [Fact]
    public async Task A_pair_that_Binance_no_longer_has_is_over_and_the_position_gets_a_new_one()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Its user takes the pair off at Binance, and Binance has neither order any more.
        application.Binance.TakeOff(application.Binance.The(stop: true));
        application.Binance.Instead = request =>
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v3/order"
                ? StandInSystem.Json(HttpStatusCode.BadRequest, """{ "code": -2013, "msg": "Order does not exist." }""")
                : null;

        // Asked a moment after the pair was placed, that says nothing yet, as of an order that
        // could still arrive: no second pair is placed beside one that Binance has just taken.
        await application.SettleAsync(agent.Id);

        Assert.Equal(
            [(OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status)));

        application.Clock.Set(application.Clock.GetUtcNow() + ExchangeOrderTimes.FinalAfter);
        await application.SettleAsync(agent.Id);

        // Both rested, and are gone: the position is protected by a new pair.
        Assert.Equal(
            [
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled),
                (OrderPurpose.StopLoss, OrderStatus.Open),
                (OrderPurpose.TakeProfit, OrderStatus.Open),
            ],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status)));
    }

    [Fact]
    public async Task A_take_profit_that_rests_without_its_stop_loss_is_taken_off_and_the_position_gets_a_new_pair()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        var settled = Assert.Single(application.Application.Reconciliations);

        // The records say a take-profit that rests alone: its stop-loss is written off, as by
        // a fault, while the pair rests at Binance.
        application.Application.Database.Execute($"UPDATE orders SET status = 'Canceled' WHERE agent_id = '{agent.Id}' AND purpose = 'StopLoss'");
        Assert.False(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        await application.SettleAsync(agent.Id);

        // No second pair is placed beside the first: what rests of the first is taken off, and
        // only then the position gets its pair anew. It never has two orders that sell the
        // same, of which one would sell what the account holds beside it (FR-BIN-S3).
        Assert.Equal(
            [
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled),
                (OrderPurpose.StopLoss, OrderStatus.Open),
                (OrderPurpose.TakeProfit, OrderStatus.Open),
            ],
            (await application.OrdersOfAsync(agent.Id)).Skip(1).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(
            ["CANCELED", "CANCELED", "NEW", "NEW"],
            application.Binance.Orders.Where(order => order.Asked.Stop).Select(order => order.Status));
        Assert.True(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task That_the_take_profit_rests_is_said_in_the_log_by_the_number_that_Binance_gave_the_order()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Beside the event of the stop-loss, with the agent and the order, and nothing else of
        // either: no price and no quantity (NFR-LOG-3).
        var said = Assert.Single(
            application.Application.Events(),
            said => said.Area == Area
                && said.Template == "The take-profit of the position of agent {AgentId} rests on the exchange as the order {ExchangeOrderId}");

        Assert.Equal(($"{agent.Id}", $"{application.Binance.TheTakeProfit().Id}"), (said.Text("AgentId"), said.Text("ExchangeOrderId")));
        Assert.Single(
            application.Application.Events(),
            said => said.Template == "The stop-loss of the position of agent {AgentId} rests on the exchange as the order {ExchangeOrderId}");
    }
}
