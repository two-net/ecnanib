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
    public async Task A_short_position_is_entered_by_selling_and_protected_by_a_stop_loss_that_buys()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync(market: Market.Futures);

        await application.PlaceAsync(agent, GuardCases.OpenShort);

        // The quantity is the one that the Risk Guard computed, and no other (FR-RSK-5).
        Assert.Equal(
            [(false, "SELL", 0.02m, null, false), (true, "BUY", 0.02m, (decimal?)61000m, true)],
            application.Binance.Asked.Select(asked => (asked.Stop, asked.Side, asked.Quantity, asked.Trigger, asked.ReduceOnly)));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((PositionSide.Short, 61000m, (decimal?)58000m), (position.Side, position.StopLoss, position.TakeProfit));
    }

    [Fact]
    public async Task A_plan_with_a_limit_entry_places_nothing_and_says_why()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();

        // An entry at a price rests until it is executed, and is protected from then on, which
        // takes the orders that each market has for it. They are not built.
        await application.PlaceAsync(agent, GuardCases.OpenLong with { EntryPrice = 59900m });

        Assert.Empty(application.Binance.Asked);
        Assert.Empty(await application.OrdersOfAsync(agent.Id));
        Assert.Equal("LimitEntry", Assert.Single(application.Application.Events(), said => said.Area == Area && said.Template == NotEntered).Text("NoEntryReason"));
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

        // The stop-loss first: on Spot it holds what the close sells. Then the close, for what
        // the position holds, as an order that only closes.
        var paths = market == Market.Spot
            ? new[] { "POST /api/v3/order", "POST /api/v3/order", "DELETE /api/v3/order", "POST /api/v3/order" }
            : ["POST /fapi/v1/order", "POST /fapi/v1/algoOrder", "DELETE /fapi/v1/algoOrder", "POST /fapi/v1/order"];
        Assert.Equal(paths, application.Binance.OrderCalls);
        Assert.Equal("CANCELED", application.Binance.The(stop: true).Status);
        Assert.Equal((false, "SELL", quantity, market == Market.Futures), Last(application.Binance.Asked));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal(ExitReason.TeamClose, position.ExitReason);
        Assert.NotNull(position.ClosedAt);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, null),
                (OrderPurpose.StopLoss, OrderStatus.Canceled, null),
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
        // closed already, and no order is placed that would open one on the other side.
        Assert.Equal(2, application.Binance.Asked.Count);
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

        // The position is open as it was, with a stop-loss that rests: a new one, at the same price.
        Assert.Equal(PositionCloseOutcome.Failed, outcome);
        Assert.Null(Assert.Single(await application.PositionsOfAsync(agent.Id)).ClosedAt);
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled),
                (OrderPurpose.StopLoss, OrderStatus.Canceled),
                (OrderPurpose.Close, OrderStatus.Rejected),
                (OrderPurpose.StopLoss, OrderStatus.Open),
            ],
            (await application.OrdersOfAsync(agent.Id)).Select(order => (order.Purpose, order.Status)));
        Assert.Equal(59000m, application.Binance.Orders[^1].Asked.Trigger);
        Assert.Equal("NEW", application.Binance.Orders[^1].Status);
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
                "POST /fapi/v1/order", "POST /fapi/v1/algoOrder",
                "DELETE /fapi/v1/algoOrder", "POST /fapi/v1/order", "POST /fapi/v1/order", "POST /fapi/v1/algoOrder",
            ],
            application.Binance.OrderCalls);
        Assert.Equal(
            [(false, "SELL", true), (false, "SELL", false), (true, "BUY", true)],
            application.Binance.Asked.Skip(2).Select(asked => (asked.Stop, asked.Side, asked.ReduceOnly)));

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
    public async Task A_plan_to_adjust_protection_moves_the_stop_loss_and_keeps_the_take_profit_as_a_price()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The stop-loss moves up, which reduces the risk, and the take-profit moves as well.
        await application.PlaceAsync(agent, GuardCases.Adjust with { TakeProfit = 63000m });

        // The stop-loss that rested is taken off, and one rests at the new price. No order is
        // placed for the take-profit: it is a price in the records.
        Assert.Equal(
            ["POST /api/v3/order", "POST /api/v3/order", "DELETE /api/v3/order", "POST /api/v3/order"],
            application.Binance.OrderCalls);
        Assert.Equal(
            [("CANCELED", (decimal?)59000m), ("NEW", 59800m)],
            application.Binance.Orders.Where(order => order.Asked.Stop).Select(order => (order.Status, order.Asked.Trigger)));

        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59800m, (decimal?)63000m, null), (position.StopLoss, position.TakeProfit, position.ClosedAt));

        // What the agent holds says the stop-loss that rests, and no take-profit, for none rests.
        var holds = await Assert.Single(application.Application.Holdings).OfAsync(agent, TestContext.Current.CancellationToken);
        Assert.Equal(new PositionContext(PositionSide.Long, 60000m, 0.01m, 59800m, TakeProfit: null), holds.Position);
        Assert.Equal([new OpenOrder(OpenOrderKind.StopLoss, OrderSide.Sell, 0.01m, 59800m)], holds.Orders);
    }

    [Fact]
    public async Task A_stop_loss_that_cannot_be_placed_at_its_new_price_goes_back_to_where_it_was()
    {
        using var application = new OrdersApplication();
        var agent = await application.GiveAgentAsync();
        await application.PlaceAsync(agent, GuardCases.OpenLong);

        // The price has fallen below the new stop-loss: Binance refuses it there.
        application.Binance.Fate = asked => asked.Stop && asked.Trigger == 59800m ? OrderFate.Refused : OrderFate.Taken;

        await application.PlaceAsync(agent, GuardCases.Adjust);

        // The position is protected at the old price again, and is not closed.
        var position = Assert.Single(await application.PositionsOfAsync(agent.Id));
        Assert.Equal((59000m, null), (position.StopLoss, position.ClosedAt));
        Assert.Equal(
            [
                (OrderPurpose.Entry, OrderStatus.Filled, null),
                (OrderPurpose.StopLoss, OrderStatus.Canceled, 59000m),
                (OrderPurpose.StopLoss, OrderStatus.Rejected, 59800m),
                (OrderPurpose.StopLoss, OrderStatus.Open, (decimal?)59000m),
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
