using System.Net;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// On any mismatch the exchange is treated as the source of truth, records are corrected, and
/// the mismatch is logged and shown (FR-BIN-7). Actions the user takes directly on Binance (for
/// example closing a position by hand) are detected by reconciliation and reflected in the
/// agent's records (FR-BIN-8). See "Reconciling" in docs/orders.md.
/// </summary>
public class ReconciliationTests
{
    private const string Area = "Ecnanib.DecisionEngine.AgentOrders";

    private const string Mismatched = "The records of agent {AgentId} in {Symbol} did not match the exchange: {Mismatch}. They were corrected";

    // Longer than an order takes to arrive: what an order did by then is in what the exchange says.
    private static readonly TimeSpan AWhile = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task An_agent_whose_account_holds_what_its_records_say_is_reconciled_and_nothing_is_corrected()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        Assert.True(await application.ReconcileAsync(agent.Id));

        // The orders were asked about, and then the account, once: it said what the records say.
        Assert.Equal(2, application.Binance.AskedAbout.Count);
        Assert.Equal(
            new AskedPosition(agent.UserId, TradingMode.Demo, "BTCUSDT"),
            Assert.Single(application.Application.Providers.FuturesPositionsAsked));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.02m, null), (position.Quantity, position.ClosedAt));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.DoesNotContain(application.Application.Events(), said => said.Area == Area && said.Level != "Information");
    }

    [Fact]
    public async Task A_position_that_its_user_closed_at_Binance_is_closed_in_the_records_and_what_protected_it_is_taken_off()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // Its user closes the position by hand, at Binance. Its stop-loss and its take-profit rest on.
        application.Binance.HoldByHand(Market.Futures, 0m);

        Assert.True(await application.ReconcileAsync(agent.Id));

        // The exchange is the source of truth: the position is closed, and no order of the agent closed it.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.ClosedAtExchange, position.ExitReason);
        Assert.NotNull(position.ClosedAt);

        // No execution says what it was sold at, so it counts as closed at the price when that
        // was found (FR-BIN-10): the price that it was entered at here, at which it made nothing.
        // See AgentResultTests for a price that has moved.
        Assert.Equal(0m, position.MadeAtExchange);

        // What protected it would act on the next position of the agent: it is taken off.
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(["FILLED", "CANCELED", "CANCELED"], application.Binance.Orders.Select(order => order.Status));

        // Nothing was placed to close it, and nothing to protect it anew: the entry and its
        // protection are all that Binance was ever asked to take.
        Assert.Equal(3, application.Binance.Asked.Count);

        // The account said it twice before it was believed.
        Assert.Equal(2, application.Application.Providers.FuturesPositionsAsked.Count);

        // Logged, without a price or a quantity, and shown to its user.
        var said = Assert.Single(application.Application.Events(), written => written.Area == Area && written.Level == "Warning");
        Assert.Equal(Mismatched, said.Template);
        Assert.Equal((agent.Id.ToString(), "BTCUSDT", "PositionClosed"), (said.Text("AgentId"), said.Text("Symbol"), said.Text("Mismatch")));

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal((NotificationType.ReconciliationMismatch, (Guid?)agent.Id), (notification.Type, notification.AgentId));
        Assert.Equal(
            "Your Binance account no longer holds the long position in BTCUSDT (Demo): it was closed at Binance, and by no order of " +
            "the agent. The records of the agent now say that it is closed, and what still rests of its stop-loss and its " +
            "take-profit is taken off the exchange.",
            notification.Content);

        // The agent is as its user left it, and holds nothing: its team decides anew.
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_position_that_its_user_reversed_at_Binance_is_closed_in_the_records()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // The account holds a short position where the agent opened a long one: nothing of the long is left.
        application.Binance.HoldByHand(Market.Futures, -0.03m);

        Assert.True(await application.ReconcileAsync(agent.Id));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((PositionSide.Long, ExitReason.ClosedAtExchange), (position.Side, position.ExitReason));
        Assert.NotNull(position.ClosedAt);
        Assert.Equal("PositionClosed", Assert.Single(application.Application.Events(), written => written.Template == Mismatched).Text("Mismatch"));
    }

    [Fact]
    public async Task A_position_that_its_user_reduced_at_Binance_holds_less_in_the_records_and_keeps_its_protection()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        application.Binance.HoldByHand(Market.Futures, 0.005m);

        Assert.True(await application.ReconcileAsync(agent.Id));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.005m, null), (position.Quantity, position.ClosedAt));

        // What left it counts as sold at the price when that was found, which is the price that
        // it was entered at here. The position says that it was corrected.
        Assert.Equal(0m, position.MadeAtExchange);

        // Its orders only reduce the position, so they close no more than the account holds: they rest on.
        Assert.Equal(["FILLED", "NEW", "NEW"], application.Binance.Orders.Select(order => order.Status));
        Assert.Equal(3, (await application.OrdersOfAsync(agent.Id)).Count);

        Assert.Equal("PositionReduced", Assert.Single(application.Application.Events(), written => written.Template == Mismatched).Text("Mismatch"));

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.ReconciliationMismatch, notification.Type);
        Assert.Equal(
            "Your Binance account holds 0.005 in the long position in BTCUSDT (Demo), where the records of the agent said 0.02: it " +
            "was reduced at Binance, and by no order of the agent. The records now say 0.005.",
            notification.Content);
    }

    [Fact]
    public async Task A_position_that_its_user_added_to_at_Binance_is_adopted_and_protected_anew_for_all_of_it()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // Its user buys more of it by hand, at another price: Binance says the average of both.
        application.Binance.HoldByHand(Market.Futures, 0.05m);
        application.Binance.EnteredAt = 60300m;

        Assert.True(await application.ReconcileAsync(agent.Id));

        // The records hold what the account holds, at the price that Binance says.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.05m, 60300m, null), (position.Quantity, position.EntryPrice, position.ClosedAt));

        // What was added to it has made nothing, and no order had closed a part of it. The
        // position says all the same that it was corrected: its result is no measured one.
        Assert.Equal(0m, position.MadeAtExchange);

        // What protected the position was for what the agent opened: it is taken off, and
        // placed anew for all that is held, at the prices that the position says.
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, 0.02m),
                (OrderPurpose.StopLoss, OrderStatus.Canceled, 0.02m),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled, 0.02m),
                (OrderPurpose.StopLoss, OrderStatus.Open, 0.05m),
                (OrderPurpose.TakeProfit, OrderStatus.Open, 0.05m),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Quantity)));
        Assert.Equal(
            [("STOP_MARKET", 0.05m, (decimal?)59000m, true), ("TAKE_PROFIT_MARKET", 0.05m, 62000m, true)],
            application.Binance.Orders.Where(order => order.Rests).Select(order => (order.Type, order.Asked.Quantity, order.Asked.Trigger, order.Asked.ReduceOnly)));

        Assert.Equal("PositionGrown", Assert.Single(application.Application.Events(), written => written.Template == Mismatched).Text("Mismatch"));

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.ReconciliationMismatch, notification.Type);
        Assert.Equal(
            "Your Binance account holds 0.05 in the long position in BTCUSDT (Demo), where the records of the agent said 0.02: it " +
            "was added to at Binance, and by no order of the agent. The records now say 0.05, the agent manages all of it, and its " +
            "stop-loss and its take-profit are placed anew for all of it.",
            notification.Content);
    }

    [Fact]
    public async Task The_account_is_believed_only_when_it_says_the_same_twice()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // Binance says once that the account holds nothing, as it may a moment after something
        // changed, and then what it holds.
        var asked = 0;
        application.Application.Providers.FuturesPosition = _ =>
            Interlocked.Increment(ref asked) == 1 ? StandInProviders.NoPosition : application.Binance.Position();
        var began = application.Clock.GetUtcNow();

        // Not reconciled: the exchange did not say for certain what the account holds.
        Assert.False(await application.ReconcileAsync(agent.Id));

        // It was asked a second time, no sooner than an order takes to arrive after the first.
        Assert.Equal(2, asked);
        Assert.True(application.Clock.GetUtcNow() >= began.AddSeconds(10));

        // Nothing was corrected, and nothing taken off: the position is open, with its protection.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.02m, null), (position.Quantity, position.ClosedAt));
        Assert.Equal(["FILLED", "NEW", "NEW"], application.Binance.Orders.Select(order => order.Status));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.DoesNotContain(application.Application.Events(), written => written.Template == Mismatched);

        // The next time the account says what the records say, and the agent is reconciled.
        Assert.True(await application.ReconcileAsync(agent.Id));
    }

    [Fact]
    public async Task An_order_that_does_not_answer_in_between_leaves_the_records_as_they_are()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        application.Binance.HoldByHand(Market.Futures, 0m);

        // Binance answers the first questions about the orders, and then no more: whether one
        // of them was executed meanwhile, which would explain the account, is not known.
        var questions = 0;
        application.Binance.Instead = request =>
            request.Method == HttpMethod.Get
            && request.RequestUri!.AbsolutePath == "/fapi/v1/algoOrder"
            && Interlocked.Increment(ref questions) > 2
                ? StandInSystem.Json(HttpStatusCode.ServiceUnavailable, """{ "code": -1001, "msg": "Internal error." }""")
                : null;

        Assert.False(await application.ReconcileAsync(agent.Id));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Null(position.ClosedAt);
        Assert.Equal(["FILLED", "NEW", "NEW"], application.Binance.Orders.Select(order => order.Status));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_stop_loss_that_was_executed_meanwhile_explains_the_account_and_is_no_mismatch()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // The stop-loss is executed just after it was asked about, and before the account is.
        var executed = false;
        application.Application.Providers.FuturesPosition = _ =>
        {
            if (!executed)
            {
                executed = true;
                application.Binance.Execute(application.Binance.The(stop: true));
            }

            return application.Binance.Position();
        };

        await application.ReconcileAsync(agent.Id);

        // The position was closed by its stop-loss, which is what the records say.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.StopLoss, position.ExitReason);
        Assert.NotNull(position.ClosedAt);

        // Nobody is told of a mismatch: there was none.
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
        Assert.DoesNotContain(application.Application.Events(), written => written.Template == Mismatched);
    }

    [Fact]
    public async Task A_position_that_was_opened_a_moment_ago_is_not_held_against_the_account_yet()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The account may not say yet what the entry bought: it is not asked, and so not believed.
        application.Application.Providers.FuturesPosition = _ => StandInProviders.NoPosition;

        Assert.False(await application.ReconcileAsync(agent.Id));

        Assert.Empty(application.Application.Providers.FuturesPositionsAsked);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
    }

    [Theory]
    [InlineData(ExchangeCallOutcome.Failed)]
    [InlineData(ExchangeCallOutcome.TimedOut)]
    [InlineData(ExchangeCallOutcome.RateLimited)]
    public async Task An_account_that_cannot_be_read_corrects_nothing(ExchangeCallOutcome outcome)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        application.Application.Providers.FuturesPosition = _ => ExchangeFuturesPosition.Without(outcome);

        // Not reconciled, and nothing is taken for a position that is gone because the exchange did not say.
        Assert.False(await application.ReconcileAsync(agent.Id));

        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Equal(["FILLED", "NEW", "NEW"], application.Binance.Orders.Select(order => order.Status));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));

        var said = Assert.Single(application.Application.Events(), written => written.Area == Area && written.Level == "Warning");
        Assert.Equal("What the account of agent {AgentId} holds in {Symbol} could not be read: {ExchangeCallOutcome}", said.Template);
        Assert.Equal(outcome.ToString(), said.Text("ExchangeCallOutcome"));
    }

    [Fact]
    public async Task A_position_that_was_closed_at_the_exchange_by_an_order_of_the_agent_says_so_once_the_order_does()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // The account holds nothing, and Binance does not take off what protected the position.
        application.Binance.HoldByHand(Market.Futures, 0m);
        application.Binance.Instead = request => request.Method == HttpMethod.Delete
            ? StandInSystem.Json(HttpStatusCode.ServiceUnavailable, """{ "code": -1001, "msg": "Internal error." }""")
            : null;

        await application.ReconcileAsync(agent.Id);

        Assert.Equal(ExitReason.ClosedAtExchange, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);

        // Its stop-loss had been executed after all: Binance says so when it is asked to take it off.
        application.Binance.Instead = null;
        application.Binance.Execute(application.Binance.The(stop: true));

        await application.SettleAsync(agent.Id);

        // The order that closed the position says why it is closed.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.StopLoss, position.ExitReason);
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Filled), (OrderPurpose.TakeProfit, OrderStatus.Canceled)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
    }

    [Fact]
    public async Task A_position_that_is_closed_for_its_user_and_that_the_account_no_longer_holds_is_said_to_be_gone()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        application.Binance.HoldByHand(Market.Futures, 0m);

        // "Close position now" asks the exchange first: there is nothing to close.
        Assert.Equal(PositionCloseOutcome.NothingOpen, await application.CloseAsync(agent.Id));

        Assert.Equal(ExitReason.ClosedAtExchange, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);

        // No order that closes was sent to an account that holds nothing.
        Assert.Equal(3, application.Binance.Asked.Count);
        Assert.Equal(NotificationType.ReconciliationMismatch, Assert.Single(await application.NotificationsOfAsync(agent.UserId)).Type);
    }

    [Fact]
    public async Task A_stop_loss_that_was_taken_off_at_Binance_is_a_mismatch_that_its_user_is_told_of()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // Its user takes the stop-loss off by hand. The position is open still.
        application.Binance.TakeOff(application.Binance.The(stop: true));

        // A position whose stop-loss does not rest is protected first: the account is held
        // against it with the next pass, so the agent is not reconciled yet.
        Assert.False(await application.ReconcileAsync(agent.Id));
        Assert.Empty(application.Application.Providers.FuturesPositionsAsked);

        // The records said that it rests: they are corrected, and the position has a stop-loss again.
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Open),
                (OrderPurpose.StopLoss, OrderStatus.Open),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));

        Assert.Equal("ProtectionTakenOff", Assert.Single(application.Application.Events(), written => written.Template == Mismatched).Text("Mismatch"));

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.ReconciliationMismatch, notification.Type);
        Assert.Equal(
            "The stop-loss of the position in BTCUSDT (Demo) no longer rested at Binance, with nothing of it executed, where the " +
            "records said that it rests. The position is open, so the service places its protection again.",
            notification.Content);

        // With the next pass the account says what the records say, and the agent is reconciled.
        Assert.True(await application.ReconcileAsync(agent.Id));
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_position_that_lost_its_stop_loss_is_protected_before_its_account_is_asked_twice()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // Its user takes the stop-loss off by hand, and sells a part of the position.
        application.Binance.TakeOff(application.Binance.The(stop: true));
        application.Binance.HoldByHand(Market.Futures, 0.005m);
        var began = application.Clock.GetUtcNow();

        Assert.False(await application.ReconcileAsync(agent.Id));

        // An account that differs is asked a second time, a while later. What is left of the
        // position does not wait that long for its stop-loss: it rests again at once, and
        // closes no more than the account holds, since it only reduces the position.
        Assert.True(application.Clock.GetUtcNow() < began.AddSeconds(10));
        Assert.Equal(OrderStatus.Open, (await application.OrdersOfAsync(agent.Id))[^1].Status);
        Assert.Equal(0.02m, Assert.Single(await application.PositionsOfAsync(agent.Id)).Quantity);

        // The next pass corrects the records.
        Assert.True(await application.ReconcileAsync(agent.Id));
        Assert.Equal(0.005m, Assert.Single(await application.PositionsOfAsync(agent.Id)).Quantity);
    }

    [Fact]
    public async Task A_position_of_the_Spot_market_that_its_user_sold_at_Binance_is_closed_in_the_records_and_not_said_to_be_unprotected()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // Its user takes the pair off by hand, which held what the position holds, and sells it.
        application.Binance.TakeOff(application.Binance.The(stop: true));
        application.Binance.HoldByHand(Market.Spot, 0m);

        // What settles the orders finds the pair gone, and Binance refuses a new one: the
        // account does not hold what it would sell.
        await application.SettleAsync(agent.Id);

        // Before the position is given up on, the account is asked: it holds nothing of it.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.ClosedAtExchange, position.ExitReason);
        Assert.NotNull(position.ClosedAt);

        // No order that closes was sent, nobody is told that a stop-loss failed, and the agent runs on.
        Assert.DoesNotContain(application.Binance.Asked, order => !order.Stop && order.Side == "SELL");
        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(NotificationType.ReconciliationMismatch, notification.Type);
        Assert.Equal(
            "Your Binance account no longer holds what the position in BTCUSDT (Demo) held: it was sold or moved at Binance, and by " +
            "no order of the agent. The records of the agent now say that the position is closed.",
            notification.Content);
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.Equal("PositionClosed", Assert.Single(application.Application.Events(), written => written.Template == Mismatched).Text("Mismatch"));
    }

    [Fact]
    public async Task A_position_of_the_Spot_market_of_which_its_user_sold_a_part_is_protected_anew_for_what_is_left()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        application.Binance.TakeOff(application.Binance.The(stop: true));
        application.Binance.HoldByHand(Market.Spot, 0.006m);

        await application.SettleAsync(agent.Id);

        // The position holds what the account holds, and its pair is for that.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.006m, null), (position.Quantity, position.ClosedAt));
        Assert.Equal(
            [("STOP_LOSS", 0.006m), ("TAKE_PROFIT", 0.006m)],
            application.Binance.Orders.Where(order => order.Rests).Select(order => (order.Type, order.Asked.Quantity)));

        // Both are said: that the account holds less, and that the pair no longer rested.
        Assert.Equal(
            ["PositionReduced", "ProtectionTakenOff"],
            application.Application.Events().Where(written => written.Template == Mismatched).Select(written => written.Text("Mismatch")));
        Assert.All(await application.NotificationsOfAsync(agent.UserId), told => Assert.Equal(NotificationType.ReconciliationMismatch, told.Type));
    }

    [Fact]
    public async Task What_two_positions_of_one_user_share_is_written_off_once_for_one_sale()
    {
        using var application = new OrdersApplication();
        var stopped = await application.GiveAgentAsync();
        await application.PlaceAsync(stopped, GuardCases.OpenLong);

        // Its user stops the agent and keeps its position, and starts another agent in the same
        // symbol, which buys as well: the two positions share what the account has of the asset.
        application.Application.Database.Execute(
            $"UPDATE agents SET state = 'Stopped', state_cause = 'UserStoppedKeepingPosition' WHERE id = '{stopped.Id}'");
        var running = await application.AgentAsync(await application.Application.GiveAgentAsync(
            stopped.UserId, application.Clock.GetUtcNow(), name: "Second", state: AgentState.Running, cause: AgentStateCause.UserStarted, team: false));
        await application.PlaceAsync(running, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // Their user takes both pairs off by hand, and sells three quarters of what the two bought.
        foreach (var stopLoss in application.Binance.Orders.Where(order => order is { Rests: true, Type: "STOP_LOSS" }))
        {
            application.Binance.TakeOff(stopLoss);
        }

        application.Binance.HoldByHand(Market.Spot, 0.005m);

        // Both are settled at the same moment, as a run of a job settles them.
        await application.WhileTimePassesAsync(Task.WhenAll(
            application.Orders.SettleAsync(stopped.Id, CancellationToken.None),
            application.Orders.SettleAsync(running.Id, CancellationToken.None)));

        // Each was worked out against what the other held. What the account still has is the
        // position of one of them, protected for what is left, and the other is closed: the
        // sale is not counted against both, which would leave what is left with no agent.
        var positions = (await application.PositionsOfAsync(stopped.Id)).Concat(await application.PositionsOfAsync(running.Id)).ToList();
        var open = Assert.Single(positions, position => position.ClosedAt is null);
        Assert.Equal(0.005m, open.Quantity);
        Assert.Equal(ExitReason.ClosedAtExchange, Assert.Single(positions, position => position.ClosedAt is not null).ExitReason);
        Assert.Equal(
            [("STOP_LOSS", 0.005m), ("TAKE_PROFIT", 0.005m)],
            application.Binance.Orders.Where(order => order.Rests).Select(order => (order.Type, order.Asked.Quantity)));
    }

    [Fact]
    public async Task What_else_the_account_holds_of_the_asset_is_not_taken_for_a_sale()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);
        application.Clock.Set(application.Clock.GetUtcNow() + AWhile);

        // Its user holds more of the asset than the agent bought, and takes the pair off by hand.
        application.Binance.TakeOff(application.Binance.The(stop: true));
        application.Binance.HoldByHand(Market.Spot, 1.5m);

        await application.SettleAsync(agent.Id);

        // The account covers the position: it is protected anew, for what the agent bought, and no more.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((0.01m, null), (position.Quantity, position.ClosedAt));
        Assert.Equal(
            [("STOP_LOSS", 0.01m), ("TAKE_PROFIT", 0.01m)],
            application.Binance.Orders.Where(order => order.Rests).Select(order => (order.Type, order.Asked.Quantity)));
    }
}
