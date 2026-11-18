using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.RiskGuard;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Every open position has a stop-loss order resting on the exchange. If the protective order
/// cannot be placed after an entry fills, the system closes the position at market at once and
/// alerts the user (FR-RSK-7). On the Spot market the stop-loss is one order of a pair with the
/// take-profit, so what holds for it there holds for the pair: see <see cref="ProtectivePairTests"/>.
/// See "The stop-loss" in docs/orders.md.
/// </summary>
public class ProtectiveStopTests
{
    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task An_entry_that_is_executed_is_followed_by_a_stop_loss_that_rests_on_the_exchange(Market market)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: market);
        var plan = market == Market.Spot ? GuardCases.OpenLong : GuardCases.OpenLongOnFutures;
        var quantity = market == Market.Spot ? 0.01m : 0.02m;

        await application.PlaceAsync(agent, plan);

        // Binance was asked twice: for the entry at the market, for the quantity that the Risk
        // Guard computed, and then for the stop-loss at the price of the plan, for what was
        // bought, which only closes the position. On Spot the stop-loss comes with the
        // take-profit of the plan, as one pair that carries an identifier of its own.
        var binance = application.Binance;
        var stopLoss = new AskedOrder(
            market, binance.Orders[1].Asked.ClientOrderId, Stop: true, "SELL", quantity, Trigger: 59000m, ReduceOnly: market == Market.Futures);

        Assert.Equal(
            [
                new AskedOrder(market, binance.Orders[0].Asked.ClientOrderId, Stop: false, "BUY", quantity, Trigger: null, ReduceOnly: false),
                market == Market.Spot
                    ? stopLoss with
                    {
                        TakeProfit = 62000m,
                        TakeProfitId = binance.TheTakeProfit().Asked.ClientOrderId,
                        PairId = binance.Orders[1].Asked.PairId,
                    }
                    : stopLoss,
            ],
            binance.Asked);
        Assert.Equal("NEW", binance.The(stop: true).Status);

        // The position is open in the records, with the stop-loss and the take-profit of the plan.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(
            (PositionSide.Long, 60000m, quantity, 59000m, (decimal?)62000m, null, agent.Mode, market, agent.Symbol),
            (position.Side, position.EntryPrice, position.Quantity, position.StopLoss, position.TakeProfit, position.ClosedAt, position.Mode,
                position.Market, position.Symbol));

        // And its orders: the entry that opened it, which is executed, and the stop-loss that
        // rests, with the take-profit that rests beside it on Spot.
        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderType.Market, OrderSide.Buy, OrderStatus.Filled, (decimal?)null),
                (OrderPurpose.StopLoss, OrderType.Stop, OrderSide.Sell, OrderStatus.Open, 59000m),
                .. market == Market.Spot
                    ? new[] { (OrderPurpose.TakeProfit, OrderType.Stop, OrderSide.Sell, OrderStatus.Open, (decimal?)62000m) }
                    : [],
            ],
            orders.Select(order => (order.Purpose, order.Type, order.Side, order.Status, order.Price)));
        Assert.All(orders, order => Assert.Equal(position.Id, order.PositionId));

        // The agent is settled: it gets its next cycle, and nobody was told anything.
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task On_Spot_the_stop_loss_is_for_what_was_bought_less_the_fee_taken_in_the_asset()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // Binance takes its fee of a purchase in what was bought: a tenth of a percent of 0.01.
        application.Binance.FeeAsset = "BTC";

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // What the account holds of the purchase is 0.00999, which is what a sale can be for:
        // an order for all of 0.01 would be refused for lack of the asset. The take-profit
        // sells the same, and no more: an agent sells only what it bought (FR-BIN-S3).
        Assert.Equal(0.00999m, application.Binance.The(stop: true).Asked.Quantity);
        Assert.Equal(0.00999m, application.Binance.TheTakeProfit().Asked.Quantity);
        Assert.Equal(0.00999m, Assert.Single(await application.PositionsOfAsync(agent.Id)).Quantity);

        // The execution is stored with its fee, as Binance said it.
        var entry = (await application.OrdersOfAsync(agent.Id))[0];
        var fill = Assert.Single(await application.FillsOfAsync(entry.Id));
        Assert.Equal((60000m, 0.01m, 0.00001m, "BTC"), (fill.Price, fill.Quantity, fill.Fee, fill.FeeAsset));
        Assert.True(entry.FillsRecorded);
    }

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task A_stop_loss_that_Binance_refuses_closes_the_position_at_market_at_once_and_notifies_the_user(Market market)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: market);
        var quantity = market == Market.Spot ? 0.01m : 0.02m;

        // The price has passed the stop-loss by the time that the entry is executed.
        application.Binance.Fate = asked => asked.Stop ? OrderFate.Refused : OrderFate.Taken;

        await application.PlaceAsync(agent, market == Market.Spot ? GuardCases.OpenLong : GuardCases.OpenLongOnFutures);

        // The entry, the stop-loss that was refused, which is not sent again, and at once the
        // order that closes the position at the market.
        Assert.Equal(
            [(false, "BUY", quantity), (true, "SELL", quantity), (false, "SELL", quantity)],
            application.Binance.Asked.Select(asked => (asked.Stop, asked.Side, asked.Quantity)));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.ProtectionFailed, position.ExitReason);
        Assert.NotNull(position.ClosedAt);

        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Rejected),
                .. market == Market.Spot ? new[] { (OrderPurpose.TakeProfit, OrderStatus.Rejected) } : [],
                (OrderPurpose.Close, OrderStatus.Filled),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));

        // Its user is told, about the agent, in plain words that hold no key.
        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal((NotificationType.ProtectionFailure, agent.Id, false), (notification.Type, notification.AgentId, notification.IsRead));
        Assert.Equal(
            "The stop-loss of the position in BTCUSDT (Demo) could not be placed, so the position was closed at market.",
            notification.Content);

        // The agent runs on: the position is closed, and nothing is left to settle.
        Assert.Equal(AgentState.Running, (await application.AgentAsync(agent.Id)).State);
        Assert.True(await Assert.Single(application.Application.Reconciliations).HasReconciledAsync(
            new RunningAgent(agent, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_position_that_can_be_neither_protected_nor_closed_has_its_agent_wait_for_its_user_and_is_tried_again()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        var settled = Assert.Single(application.Application.Reconciliations);

        // Binance takes the entry, and refuses everything after it.
        application.Binance.Fate = asked => asked.Side == "BUY" ? OrderFate.Taken : OrderFate.Refused;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The position is open with no stop-loss. Its user is told that, once, and the agent
        // waits for its user: it opens nothing until its user has looked.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Null(position.ClosedAt);
        Assert.NotNull(position.AlertedAt);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Rejected),
                (OrderPurpose.TakeProfit, OrderStatus.Rejected),
                (OrderPurpose.Close, OrderStatus.Rejected),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));

        var notification = Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(
            "The stop-loss of the position in BTCUSDT (Demo) could not be placed, and the position could not be closed at market " +
            "either. It is open with no stop-loss. Close it with \"Close position now\", or at Binance.",
            notification.Content);

        var waiting = await application.AgentAsync(agent.Id);
        Assert.Equal((AgentState.AttentionRequired, AgentStateCause.PositionUnprotected), (waiting.State, waiting.StateCause));
        Assert.False(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // What settles the orders tries again, and says nothing more while nothing changes.
        await application.SettleAsync(agent.Id);

        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);

        // Once Binance takes orders again, the stop-loss is placed, and rests.
        application.Binance.Fate = null;
        await application.SettleAsync(agent.Id);

        Assert.Equal("NEW", application.Binance.The(stop: true).Status);
        Assert.Equal(OrderStatus.Open, (await application.OrdersOfAsync(agent.Id))[^1].Status);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).AlertedAt);
        Assert.True(await settled.HasReconciledAsync(new RunningAgent(agent, []), TestContext.Current.CancellationToken));

        // The agent stays as it is until its user resumes it.
        Assert.Equal(AgentState.AttentionRequired, (await application.AgentAsync(agent.Id)).State);
    }

    [Fact]
    public async Task An_agent_that_its_user_paused_is_not_moved_when_its_position_is_left_unprotected()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Its user pauses the agent, takes the stop-loss off at Binance, and Binance refuses
        // every order after that.
        await using (var context = await application.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlAsync(
                $"UPDATE agents SET state = 'Paused', state_cause = 'UserPaused' WHERE id = {agent.Id}", TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        application.Binance.TakeOff(application.Binance.The(stop: true));
        application.Binance.Fate = _ => OrderFate.Refused;

        await application.SettleAsync(agent.Id);

        // Its user is told, and the agent stays paused: it is not made to look as if it could
        // be resumed for this.
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
        Assert.Equal(AgentState.Paused, (await application.AgentAsync(agent.Id)).State);
    }

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task A_stop_loss_that_was_executed_closes_the_position_in_the_records(Market market)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: market);
        await application.PlaceAsync(agent, market == Market.Spot ? GuardCases.OpenLong : GuardCases.OpenLongOnFutures);

        // The price reaches the stop-loss, and Binance executes it. Nothing tells the service.
        application.Binance.Execute(application.Binance.The(stop: true));
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);

        // The next time that the orders of the agent are settled, the stop-loss is asked about.
        await application.SettleAsync(agent.Id);

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.StopLoss, position.ExitReason);
        Assert.NotNull(position.ClosedAt);

        // Its executions are stored with it: on the Futures market under the order that the
        // stop became when it was triggered.
        var stopLoss = (await application.OrdersOfAsync(agent.Id))[1];
        Assert.Equal((OrderStatus.Filled, true), (stopLoss.Status, stopLoss.FillsRecorded));
        Assert.Equal(59000m, Assert.Single(await application.FillsOfAsync(stopLoss.Id)).Price);

        // Nothing is placed for a position that is closed, and nobody is told anything.
        await application.SettleAsync(agent.Id);

        Assert.Equal(2, application.Binance.Asked.Count);
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task A_stop_loss_that_was_taken_off_at_Binance_is_placed_again()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Its user takes the stop-loss off by hand, at Binance.
        var first = application.Binance.The(stop: true);
        application.Binance.TakeOff(first);

        await application.SettleAsync(agent.Id);

        // The take-profit went with it at Binance, for the two are one pair. The position has
        // a stop-loss again: a new order, with an identifier of its own, in a new pair.
        var orders = await application.OrdersOfAsync(agent.Id);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled),
                (OrderPurpose.StopLoss, OrderStatus.Open),
                (OrderPurpose.TakeProfit, OrderStatus.Open),
            ],
            orders.Select(order => (order.Purpose, order.Status)));
        Assert.NotEqual(orders[1].ClientOrderId, orders[3].ClientOrderId);
        Assert.NotEqual(orders[1].PairId, orders[3].PairId);
        Assert.Equal("NEW", Assert.Single(application.Binance.Orders, order => order.Asked.ClientOrderId == orders[3].ClientOrderId).Status);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
    }

    [Fact]
    public async Task A_cycle_that_is_stopped_once_its_entry_is_sent_still_places_the_stop_loss()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The cycle is stopped while Binance has the entry: the next close is near, or the
        // application is shutting down.
        using var stopping = new CancellationTokenSource();
        application.Binance.Fate = asked =>
        {
            if (!asked.Stop)
            {
                stopping.Cancel();
            }

            return OrderFate.Taken;
        };

        await application.PlaceAsync(agent, GuardCases.OpenLong, stopping.Token);

        // The call that placed the entry was not given up, and the entry is not left without
        // its protection (NFR-REL-5).
        Assert.Equal(
            [(OrderPurpose.Entry, OrderStatus.Filled), (OrderPurpose.StopLoss, OrderStatus.Open), (OrderPurpose.TakeProfit, OrderStatus.Open)],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal("NEW", application.Binance.The(stop: true).Status);
    }

    [Fact]
    public async Task What_a_cycle_that_is_stopped_leaves_undone_is_done_by_what_settles_the_orders()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The cycle is stopped while Binance has the entry, and Binance refuses the stop-loss.
        using var stopping = new CancellationTokenSource();
        application.Binance.Fate = asked =>
        {
            if (!asked.Stop)
            {
                stopping.Cancel();

                return OrderFate.Taken;
            }

            return OrderFate.Refused;
        };

        await application.PlaceAsync(agent, GuardCases.OpenLong, stopping.Token);

        // The stop-loss was sent once. A cycle that is being stopped begins nothing more, so
        // the position is open without one for the moment.
        Assert.Equal([false, true], application.Binance.Asked.Select(asked => asked.Stop));
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);

        // The job that settles the orders finds the agent, tries the stop-loss, and closes the
        // position at market when Binance refuses it again.
        await application.Application.SettleOrdersAsync(application.Clock.GetUtcNow());

        Assert.Equal(ExitReason.ProtectionFailed, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
        Assert.Equal(
            [(false, "BUY"), (true, "SELL"), (true, "SELL"), (false, "SELL")],
            application.Binance.Asked.Select(asked => (asked.Stop, asked.Side)));
        Assert.Single(await application.NotificationsOfAsync(agent.UserId));
    }
}
