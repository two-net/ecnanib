using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// ADJUST_STOP and ADJUST_TARGET (FR-CYC-03): the position's stop moves only toward less risk (FR-RISK-06), its target
/// moves or goes, and each new order cites the adjustment's cycle (AC-01) under a client order ID no earlier order had
/// (FR-EXE-04). On Futures the new stop rests before the old one is cancelled. On Spot the OCO list is replaced.
/// </summary>
public sealed class AdjustmentTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_Futures_stop_is_moved_by_placing_the_new_one_before_cancelling_the_old_and_the_user_is_told()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.MoveStop(Market.Futures, ExecutionApp.Held(placed.Quantity), 59_900m), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Adjusted, result.Status);
        Assert.Equal([$"Resting {Q(placed.Quantity)} 59900 60400"], await app.PartsAsync(agent.Id));
        var stops = app.Book.SentTo(FakeOrderBook.FuturesAlgo).Where(sent => sent.Type == "STOP_MARKET").ToList();
        Assert.Equal([59_800m, 59_900m], stops.Select(sent => sent.StopPrice!.Value));
        Assert.Equal("CANCELED", app.Book.Find(placed.Stop)!.Status);
        Assert.Equal("NEW", app.Book.Find(stops[1].ClientOrderId)!.Status);
        Assert.Equal([stops[1].ClientOrderId], result.ClientOrderIds);
        Assert.Equal(["TradeOpened", "StopMoved"], await app.NotificationsAsync(user.Id));
        Assert.Equal("NEW", app.Book.Find(placed.Target)!.Status);
    }

    [Fact]
    public async Task A_Spot_stop_is_moved_by_replacing_the_OCO_list()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.MoveStop(Market.Spot, ExecutionApp.Held(0.01664m), 59_900m), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Adjusted, result.Status);
        Assert.Equal(["Resting 0.01664 59900 60400"], await app.PartsAsync(agent.Id));
        var lists = app.Book.SentTo(FakeOrderBook.SpotList);
        Assert.Equal([(59_800m, 60_400m), (59_900m, 60_400m)], lists.Select(sent => (sent.StopPrice!.Value, sent.Price!.Value)));
        Assert.Single(app.Book.Orders, order => order is { Type: "STOP_LOSS", Status: "NEW" });
        Assert.Equal(["TradeOpened", "StopMoved"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_Futures_target_is_moved_and_the_old_one_cancelled_without_touching_the_stop()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.MoveTarget(Market.Futures, ExecutionApp.Held(placed.Quantity), 60_600m), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Adjusted, result.Status);
        await ExecutionApp.UntilAsync(() => Task.FromResult(app.Book.Orders.Any(order => order is { Type: "TAKE_PROFIT_MARKET", Status: "NEW", StopPrice: 60_600m })));
        Assert.Equal([$"Resting {Q(placed.Quantity)} 59800 60600"], await app.PartsAsync(agent.Id));
        Assert.Equal("CANCELED", app.Book.Find(placed.Target)!.Status);
        Assert.Equal("NEW", app.Book.Find(placed.Stop)!.Status);
        Assert.Equal(["TradeOpened"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_target_removed_is_cancelled_and_only_the_stop_rests()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.MoveTarget(Market.Futures, ExecutionApp.Held(placed.Quantity), null), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Adjusted, result.Status);
        Assert.Equal([$"Resting {Q(placed.Quantity)} 59800 -"], await app.PartsAsync(agent.Id));
        Assert.Equal("CANCELED", app.Book.Find(placed.Target)!.Status);
        Assert.Single(app.Book.SentTo(FakeOrderBook.FuturesAlgo), sent => sent.Type == "TAKE_PROFIT_MARKET");
    }

    [Fact]
    public async Task A_stop_is_never_widened_even_when_the_adjustment_was_sized_on_an_older_one()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        var sent = app.Book.Placed.Count;

        // Sized as if the stop were at 59,700, so 59,750 passed as tighter. The stop rests at 59,800.
        var stale = ExecutionApp.MoveStop(Market.Futures, ExecutionApp.Held(placed.Quantity, stop: 59_700m), 59_750m);
        var result = await app.ExecuteAsync(app.Request(user, agent, stale, close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Adjusted, result.Status);
        Assert.Equal([$"Resting {Q(placed.Quantity)} 59800 60400"], await app.PartsAsync(agent.Id));
        Assert.Equal(sent, app.Book.Placed.Count);
    }

    [Fact]
    public async Task A_moved_stop_the_price_has_already_passed_closes_the_position_at_market()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesAlgo, Fault.Rejected(-2021, "Order would immediately trigger."));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.MoveStop(Market.Futures, ExecutionApp.Held(placed.Quantity), 59_900m), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Closed, result.Status);
        Assert.Equal([$"Closed {Q(placed.Quantity)} 59900 60400 Unprotected"], await app.PartsAsync(agent.Id));
        Assert.Equal(0m, app.Book.FuturesAmount);
        Assert.All(app.Book.Orders.Where(order => order.Algo), order => Assert.Equal("CANCELED", order.Status));
    }

    [Fact]
    public async Task Fifty_trailing_adjustments_each_send_one_new_stop_under_an_ID_no_other_order_had()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, target: false)));
        var quantity = app.FuturesLong().Quantity;

        for (var step = 1; step <= 50; step++)
        {
            var stop = ExecutionApp.Stop + step;
            var adjust = ExecutionApp.MoveStop(Market.Futures, ExecutionApp.Held(quantity, stop: stop - 1m, target: null), stop);
            var result = await app.ExecuteAsync(app.Request(user, agent, adjust, close: app.LastClose.AddHours(step)));

            Assert.Equal(ExecutionStatus.Adjusted, result.Status);
        }

        var stops = app.Book.SentTo(FakeOrderBook.FuturesAlgo);
        Assert.Equal(51, stops.Count);
        Assert.Equal(51, stops.Select(sent => sent.ClientOrderId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(59_850m, Assert.Single(app.Book.Orders, order => order is { Type: "STOP_MARKET", Status: "NEW" }).StopPrice);
        Assert.Equal([$"Resting {Q(quantity)} 59850 -"], await app.PartsAsync(agent.Id));
    }

    private static string Q(decimal value) => value.ToString("0.#############", System.Globalization.CultureInfo.InvariantCulture);
}
