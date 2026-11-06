using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// FR-EXE-06: reconciliation brings the agent's records in line with Binance. A stop or target that executed ends its
/// protection, and on Futures the other is cancelled at once. A change made outside Ecnanib, such as the user closing
/// the position or cancelling a stop, is detected, recorded and notified. A stop the user cancelled is placed again,
/// unless the agent is stopped (FR-AGT-07). These run the sweep, which every cycle and the startup pass share.
/// </summary>
public sealed class ReconciliationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_Futures_stop_that_executed_ends_its_protection_cancels_the_take_profit_and_tells_the_user()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        Assert.Equal(ExecutionStatus.Protected, (await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)))).Status);
        var stop = app.Book.Orders.Single(order => order.Type == "STOP_MARKET");
        var target = app.Book.Orders.Single(order => order.Type == "TAKE_PROFIT_MARKET");

        app.Book.Trigger(stop.ClientOrderId);
        await SweepAsync(app);

        Assert.Equal([$"Ended {Q(stop.Quantity)} 59800 60400 StopFilled"], await app.PartsAsync(agent.Id));
        Assert.Equal("CANCELED", target.Status);
        Assert.Equal(["TradeOpened", "TradeClosed"], await app.NotificationsAsync(user.Id));
        Assert.Equal(0L, await app.CountAsync(agent.Id, "status IN ('Open', 'PartiallyFilled', 'Triggered')"));
    }

    [Fact]
    public async Task A_Spot_target_that_executed_ends_its_protection_and_its_OCO_list_is_done()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));

        app.Book.Trigger(app.Book.Orders.Single(order => order.Type == "LIMIT_MAKER").ClientOrderId);
        await SweepAsync(app);

        Assert.Equal(["Ended 0.01664 59800 60400 TargetFilled"], await app.PartsAsync(agent.Id));
        Assert.Equal(["TradeOpened", "TradeClosed"], await app.NotificationsAsync(user.Id));
        Assert.Single(app.Book.SentTo(FakeOrderBook.SpotList));
    }

    [Fact]
    public async Task A_Futures_position_closed_outside_Ecnanib_is_recorded_as_such_and_its_orders_are_cancelled()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var stop = app.Book.Orders.Single(order => order.Type == "STOP_MARKET");

        app.Book.TradeOutside(futures: true, "SELL", stop.Quantity);
        await SweepAsync(app);

        Assert.Equal([$"Ended {Q(stop.Quantity)} 59800 60400 ClosedOutside"], await app.PartsAsync(agent.Id));
        Assert.All(app.Book.Orders.Where(order => order.Algo), order => Assert.Equal("CANCELED", order.Status));
        Assert.Equal(["TradeOpened", "TradeClosed"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_Futures_position_partly_closed_outside_Ecnanib_keeps_a_stop_for_what_is_left()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var stop = app.Book.Orders.Single(order => order.Type == "STOP_MARKET");
        var left = stop.Quantity - 0.005m;

        app.Book.TradeOutside(futures: true, "SELL", 0.005m);
        await SweepAsync(app);

        await ExecutionApp.UntilAsync(async () => (await app.PartsAsync(agent.Id)).Contains($"Resting {Q(left)} 59800 60400"));
        Assert.Equal(
            [$"Ended {Q(stop.Quantity)} 59800 60400 Split", "Ended 0.005 59800 60400 ClosedOutside", $"Resting {Q(left)} 59800 60400"],
            await app.PartsAsync(agent.Id));
        Assert.Equal("CANCELED", stop.Status);
        var stops = app.Book.Orders.Where(order => order.Type == "STOP_MARKET" && order.Status == "NEW").ToList();
        Assert.Equal(left, Assert.Single(stops).Quantity);
        Assert.Contains("TradeClosed", await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_Spot_stop_cancelled_outside_Ecnanib_is_placed_again_and_the_user_is_told()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));

        app.Book.CancelOutside(app.Book.Orders.Single(order => order.Type == "STOP_LOSS").ClientOrderId);
        await SweepAsync(app);

        await ExecutionApp.UntilAsync(async () => (await app.PartsAsync(agent.Id)).SequenceEqual(["Resting 0.01664 59800 60400"]));
        Assert.Equal(2, app.Book.SentTo(FakeOrderBook.SpotList).Count);
        Assert.Single(app.Book.Orders, order => order.Type == "STOP_LOSS" && order.Status == "NEW");
        Assert.Equal(["TradeOpened", "StopMoved"], await app.NotificationsAsync(user.Id));
        Assert.All(app.Book.Placed, sent => Assert.Equal(1, app.Book.SendsOf(sent.ClientOrderId)));
    }

    [Fact]
    public async Task A_Spot_position_sold_outside_Ecnanib_is_recorded_as_closed_outside_and_nothing_is_sent()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));
        var sent = app.Book.Placed.Count;

        app.Book.CancelOutside(app.Book.Orders.Single(order => order.Type == "STOP_LOSS").ClientOrderId);
        app.Book.TradeOutside(futures: false, "SELL", 0.01664m);
        await SweepAsync(app);

        Assert.Equal(["Ended 0.01664 59800 60400 ClosedOutside"], await app.PartsAsync(agent.Id));
        Assert.Equal(sent, app.Book.Placed.Count);
        Assert.Equal(["TradeOpened", "TradeClosed"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_stopped_agents_stop_cancelled_outside_Ecnanib_is_left_to_the_user_with_its_other_orders()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE agents SET status = 'Stopped' WHERE id = '{agent.Id}'", ExecutionApp.Cancellation);
        var stop = app.Book.Orders.Single(order => order.Type == "STOP_MARKET");
        var target = app.Book.Orders.Single(order => order.Type == "TAKE_PROFIT_MARKET");
        var sent = app.Book.Placed.Count;

        app.Book.CancelOutside(stop.ClientOrderId);
        await SweepAsync(app);

        Assert.Equal([$"Ended {Q(stop.Quantity)} 59800 60400 LeftToUser"], await app.PartsAsync(agent.Id));
        Assert.Equal("NEW", target.Status);
        Assert.Equal(sent, app.Book.Placed.Count);
        Assert.Contains("TradeClosed", await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_Futures_stop_still_executing_is_not_taken_for_a_close_outside_Ecnanib()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var stop = app.Book.Orders.Single(order => order.Type == "STOP_MARKET");

        // Triggered, and the position already shrinking, but the order hasn't finished.
        stop.Status = "TRIGGERED";
        app.Book.FuturesPositionOwned = -stop.Quantity;
        await SweepAsync(app);

        Assert.Equal([$"Resting {Q(stop.Quantity)} 59800 60400"], await app.PartsAsync(agent.Id));

        app.Book.FuturesPositionOwned = 0m;
        app.Book.Trigger(stop.ClientOrderId);
        await SweepAsync(app);

        Assert.Equal([$"Ended {Q(stop.Quantity)} 59800 60400 StopFilled"], await app.PartsAsync(agent.Id));
    }

    [Fact]
    public async Task Nothing_is_recorded_while_Binance_cant_list_the_open_orders_and_it_is_once_it_can()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var stop = app.Book.Orders.Single(order => order.Type == "STOP_MARKET");

        app.Book.Trigger(stop.ClientOrderId);
        app.Book.Fail(HttpMethod.Get, FakeOrderBook.FuturesOpenAlgoOrders, Fault.ServerError(accepted: false));
        await SweepAsync(app);

        Assert.Equal([$"Resting {Q(stop.Quantity)} 59800 60400"], await app.PartsAsync(agent.Id));

        await SweepAsync(app);

        Assert.Equal([$"Ended {Q(stop.Quantity)} 59800 60400 StopFilled"], await app.PartsAsync(agent.Id));
    }

    [Fact]
    public async Task An_agent_with_nothing_on_Binance_costs_no_requests()
    {
        await using var app = new ExecutionApp(postgres);
        await app.AgentAsync(Market.Futures);
        var before = app.Agents.Binance.Requests.Count;

        await SweepAsync(app);

        Assert.Equal(before, app.Agents.Binance.Requests.Count);
    }

    private static async Task SweepAsync(ExecutionApp app) =>
        Assert.Equal(JobStatus.Succeeded, await app.Agents.RunJobAsync("ReconcileSweep"));

    private static string Q(decimal value) => value.ToString("0.#############", System.Globalization.CultureInfo.InvariantCulture);
}
