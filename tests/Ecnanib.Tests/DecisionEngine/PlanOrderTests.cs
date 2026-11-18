using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Shared;
using Ecnanib.Tests.ExchangeGateway;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.RiskGuard;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The Exchange Gateway places the entry and the protective orders on Binance, or does nothing
/// (SRS 3.2, step 7): what is placed for each plan that the Risk Guard accepted, and when
/// nothing is. See "What a plan places" in docs/orders.md.
/// </summary>
public class PlanOrderTests
{
    private const string Area = "Ecnanib.DecisionEngine.AgentOrders";

    private const string NotEntered = "No entry was placed for agent {AgentId} for the close at {ClosedAt}: {NoEntryReason}";

    [Fact]
    public async Task A_short_position_is_entered_by_selling_and_protected_by_a_stop_loss_and_a_take_profit_that_buy()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        await application.PlaceAsync(agent, GuardCases.OpenShort);

        // The quantity is the one that the Risk Guard computed, and no other (FR-RSK-5). The
        // stop-loss above the price and the take-profit below it buy what was sold, and only
        // reduce the position (FR-BIN-F1, FR-BIN-F3).
        Assert.Equal(
            [
                ("MARKET", "SELL", 0.02m, null, false),
                ("STOP_MARKET", "BUY", 0.02m, (decimal?)61000m, true),
                ("TAKE_PROFIT_MARKET", "BUY", 0.02m, 58000m, true),
            ],
            application.Binance.Asked.Select(asked => (asked.Type, asked.Side, asked.Quantity, asked.Trigger, asked.ReduceOnly)));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((PositionSide.Short, 61000m, (decimal?)58000m), (position.Side, position.StopLoss, position.TakeProfit));
    }

    [Fact]
    public async Task No_entry_is_placed_while_new_entries_are_paused()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The clock of the service is not near Binance's, or was not read (FR-BIN-5).
        application.Entries.Paused = true;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("EntriesPaused", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));

        // Once they go on, the same plan is placed.
        application.Entries.Paused = false;
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Equal(2, application.Binance.Asked.Count);
    }

    [Theory]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.Stopped)]
    [InlineData(AgentState.AttentionRequired)]
    public async Task No_entry_is_placed_for_an_agent_that_no_longer_opens_positions(AgentState state)
    {
        using var application = new OrdersApplication();

        // Its user paused or stopped the agent, or it came to wait for its user, while its
        // team decided: the cycle was handed the agent as it ran.
        var agent = await application.GiveAgentAsync(state: state);

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("NotRunning", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
    }

    [Fact]
    public async Task No_entry_is_placed_in_a_symbol_that_takes_no_stop()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // The symbol takes no stop-loss that is placed at the market: a position in it could
        // not be protected (FR-RSK-7).
        application.Binance.Instead = request => request.RequestUri!.AbsolutePath == "/api/v3/exchangeInfo"
            ? StandInSystem.Json(System.Net.HttpStatusCode.OK, BinanceCalls.SpotSymbols.Replace("\"STOP_LOSS\", ", string.Empty, StringComparison.Ordinal))
            : null;

        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Empty(application.Binance.Asked);
        Assert.Equal("NoStopOrders", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
    }

    [Fact]
    public async Task An_agent_that_holds_a_position_is_given_no_second_entry()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // An agent holds at most one position (FR-CYC-5). The Risk Guard rejects such a plan:
        // what places the orders holds to it as well, under the lock of the agent's account.
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        Assert.Equal(2, application.Binance.Asked.Count);
        Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal("Holding", Assert.Single(application.Application.Events(), said => said.Template == NotEntered).Text("NoEntryReason"));
    }

    [Theory]
    [InlineData(Market.Spot)]
    [InlineData(Market.Futures)]
    public async Task A_plan_to_close_takes_the_stop_loss_off_the_exchange_and_closes_the_position_at_market(Market market)
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: market);
        var quantity = market == Market.Spot ? 0.01m : 0.02m;
        await application.PlaceAsync(agent, market == Market.Spot ? GuardCases.OpenLong : GuardCases.OpenLongOnFutures);

        await application.PlaceAsync(agent, GuardCases.Close with { Market = market });

        // The protection first: on Spot it holds what the close sells, and is taken off as the
        // one pair that it is. On Futures the take-profit and the stop-loss are two orders, and
        // each is taken off, the take-profit first. Then the close, for what the position
        // holds, as an order that only closes.
        var paths = market == Market.Spot
            ? new[] { "POST /api/v3/order", "POST /api/v3/orderList/oco", "DELETE /api/v3/orderList", "POST /api/v3/order" }
            :
            [
                "POST /fapi/v1/order", "POST /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder",
                "DELETE /fapi/v1/algoOrder", "DELETE /fapi/v1/algoOrder", "POST /fapi/v1/order",
            ];
        Assert.Equal(paths, application.Binance.OrderCalls);
        Assert.All(application.Binance.Orders.Where(order => order.Asked.Stop), order => Assert.Equal("CANCELED", order.Status));
        Assert.Equal((false, "SELL", quantity, market == Market.Futures), Last(application.Binance.Asked));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.TeamClose, position.ExitReason);
        Assert.NotNull(position.ClosedAt);

        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, null),
                (OrderPurpose.StopLoss, OrderStatus.Canceled, null),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled, null),
                (OrderPurpose.Close, OrderStatus.Filled, (ExitReason?)ExitReason.TeamClose),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.ExitReason)));

        static (bool Stop, string Side, decimal Quantity, bool ReduceOnly) Last(IReadOnlyList<AskedOrder> asked) =>
            (asked[^1].Stop, asked[^1].Side, asked[^1].Quantity, asked[^1].ReduceOnly);
    }

    [Fact]
    public async Task A_close_of_a_position_whose_stop_loss_was_executed_meanwhile_sells_nothing()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The stop-loss is executed while the team decides to close.
        application.Binance.Execute(application.Binance.The(stop: true));

        await application.PlaceAsync(agent, GuardCases.Close with { Market = Market.Futures });

        // Binance has no stop-loss to take off, and says what became of it: the position is
        // closed already, and no order is placed that would open one on the other side. The
        // take-profit, which Binance left resting, is taken off.
        Assert.Equal(3, application.Binance.Asked.Count);
        Assert.Equal("CANCELED", application.Binance.TheTakeProfit().Status);
        Assert.Equal(ExitReason.StopLoss, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);
    }

    [Fact]
    public async Task A_position_that_cannot_be_closed_gets_its_stop_loss_back()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Binance refuses the order that closes the position, and takes a stop-loss.
        application.Binance.Fate = asked => asked.Stop ? OrderFate.Taken : OrderFate.Refused;

        var outcome = await application.CloseAsync(agent.Id);

        // The position is open as it was, with a stop-loss that rests: a new one, at the same
        // price, and on Spot its take-profit with it, as a new pair.
        Assert.Equal(PositionCloseOutcome.Failed, outcome);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled),
                (OrderPurpose.Close, OrderStatus.Rejected),
                (OrderPurpose.StopLoss, OrderStatus.Open),
                (OrderPurpose.TakeProfit, OrderStatus.Open),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(
            [("NEW", (decimal?)59000m), ("NEW", 62000m)],
            application.Binance.Orders.TakeLast(2).Select(order => (order.Status, order.Asked.Trigger)));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task The_user_of_an_agent_closes_its_position_at_market_and_a_second_close_finds_nothing_open()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // Asked twice at the same moment, as from two browsers.
        var outcomes = await Task.WhenAll(application.CloseAsync(agent.Id), application.CloseAsync(agent.Id));

        // One of them closed the position, and the other found nothing open: one order was sent.
        Assert.Equal([PositionCloseOutcome.Closed, PositionCloseOutcome.NothingOpen], outcomes.Order());
        Assert.Single(application.Binance.Asked, asked => !asked.Stop && asked.Side == "SELL");
        Assert.Equal(ExitReason.UserClose, Assert.Single(await application.PositionsOfAsync(agent.Id)).ExitReason);

        // And an agent that holds nothing has nothing to close.
        Assert.Equal(PositionCloseOutcome.NothingOpen, await application.CloseAsync(agent.Id));
    }

    [Fact]
    public async Task A_plan_to_reverse_closes_the_position_and_opens_one_on_the_other_side()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        await application.PlaceAsync(agent, GuardCases.Reverse);

        // The long position is closed, and then the short one is entered and protected.
        Assert.Equal(
            [
                "POST /fapi/v1/order", "POST /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder",
                "DELETE /fapi/v1/algoOrder", "DELETE /fapi/v1/algoOrder", "POST /fapi/v1/order",
                "POST /fapi/v1/order", "POST /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder",
            ],
            application.Binance.OrderCalls);
        Assert.Equal(
            [("MARKET", "SELL", true), ("MARKET", "SELL", false), ("STOP_MARKET", "BUY", true), ("TAKE_PROFIT_MARKET", "BUY", true)],
            application.Binance.Asked.Skip(3).Select(asked => (asked.Type, asked.Side, asked.ReduceOnly)));

        var positions = await application.PositionsOfAsync(agent.Id);
        Assert.Equal(
            [(PositionSide.Long, ExitReason.TeamClose), (PositionSide.Short, (ExitReason?)null)],
            positions.Select(position => (position.Side, position.ExitReason)));
        Assert.Equal((61000m, (decimal?)58000m), (positions[1].StopLoss, positions[1].TakeProfit));
    }

    [Fact]
    public async Task A_plan_to_reverse_opens_nothing_when_the_position_could_not_be_closed()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // Binance refuses the order that closes the long position.
        application.Binance.Fate = asked => !asked.Stop && asked.ReduceOnly ? OrderFate.Refused : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.Reverse);

        // No short position is entered beside the long one, which has its stop-loss again.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((PositionSide.Long, null), (position.Side, position.ClosedAt));
        Assert.DoesNotContain(application.Binance.Asked, asked => !asked.Stop && asked.Side == "SELL" && !asked.ReduceOnly);
        Assert.Equal(OrderStatus.Open, (await application.OrdersOfAsync(agent.Id))[^1].Status);
    }

    [Fact]
    public async Task A_plan_to_adjust_protection_on_the_Futures_market_moves_the_stop_loss_and_the_take_profit_each_as_its_own_order()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);
        await application.PlaceAsync(agent, GuardCases.OpenLongOnFutures);

        // The stop-loss moves up, which reduces the risk, and the take-profit moves as well.
        await application.PlaceAsync(agent, GuardCases.Adjust with { Market = Market.Futures, TakeProfit = 63000m });

        // The stop-loss that rested is taken off, and one rests at the new price, while the
        // take-profit rests on. Then the take-profit is taken off, and one rests at its new
        // price, while the stop-loss rests: the two are orders of their own there (FR-BIN-F3).
        Assert.Equal(
            [
                "POST /fapi/v1/order", "POST /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder",
                "DELETE /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder", "DELETE /fapi/v1/algoOrder", "POST /fapi/v1/algoOrder",
            ],
            application.Binance.OrderCalls);
        Assert.Equal(
            [
                ("STOP_MARKET", "CANCELED", (decimal?)59000m), ("TAKE_PROFIT_MARKET", "CANCELED", 62000m),
                ("STOP_MARKET", "NEW", 59800m), ("TAKE_PROFIT_MARKET", "NEW", 63000m),
            ],
            application.Binance.Orders.Where(order => order.Asked.Stop).Select(order => (order.Type, order.Status, order.Asked.Trigger)));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59800m, (decimal?)63000m, null), (position.StopLoss, position.TakeProfit, position.ClosedAt));

        // What the agent holds says the stop-loss and the take-profit that rest.
        var holds = await Assert.Single(application.Application.Holdings).OfAsync(agent, TestContext.Current.CancellationToken);
        Assert.Equal(new PositionContext(PositionSide.Long, 60000m, 0.02m, 59800m, TakeProfit: 63000m), holds.Position);
        Assert.Equal(
            [new OpenOrder(OpenOrderKind.StopLoss, OrderSide.Sell, 0.02m, 59800m), new OpenOrder(OpenOrderKind.TakeProfit, OrderSide.Sell, 0.02m, 63000m)],
            holds.Orders);
    }

    [Fact]
    public async Task A_stop_loss_that_cannot_be_placed_at_its_new_price_goes_back_to_where_it_was()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The price has fallen below the new stop-loss: Binance refuses it there, and on Spot
        // the take-profit with it, for the two are one pair.
        application.Binance.Fate = asked => asked.Stop && asked.Trigger == 59800m ? OrderFate.Refused : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.Adjust);

        // The position is protected at the old price again, and is not closed.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59000m, (decimal?)62000m, null), (position.StopLoss, position.TakeProfit, position.ClosedAt));
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, null),
                (OrderPurpose.StopLoss, OrderStatus.Canceled, 59000m),
                (OrderPurpose.TakeProfit, OrderStatus.Canceled, 62000m),
                (OrderPurpose.StopLoss, OrderStatus.Rejected, 59800m),
                (OrderPurpose.TakeProfit, OrderStatus.Rejected, 62000m),
                (OrderPurpose.StopLoss, OrderStatus.Open, 59000m),
                (OrderPurpose.TakeProfit, OrderStatus.Open, (decimal?)62000m),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status, order.Price)));
        Assert.Empty(await application.NotificationsOfAsync(agent.UserId));
    }

    [Fact]
    public async Task Orders_are_placed_only_for_a_plan_that_the_Risk_Guard_accepted()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // A plan without a stop-loss is one that the Risk Guard rejects.
        var rejected = Ecnanib.RiskGuard.GuardCheck.Of(GuardCases.OpenLong with { StopLoss = null });
        Assert.False(rejected.IsAccepted);

        await Assert.ThrowsAsync<ArgumentException>(
            () => application.Orders.PlaceAsync(application.CycleOf(agent), rejected, TestContext.Current.CancellationToken));

        Assert.Empty(application.Binance.System.Calls);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
    }
}
