using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// SELL and CLOSE (FR-CYC-03): an exit sells at market what the Risk Engine sized, never more than is held, under its
/// own cycle's evaluation (AC-01), once (FR-EXE-05). It takes the position's parts oldest first, and a part it takes
/// only some of keeps a stop for the rest (FR-EXE-02). On Spot the stop or OCO list is cancelled before the sell.
/// </summary>
public sealed class ExitTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_Futures_close_sells_the_whole_position_reduce_only_and_cancels_its_stop_and_target()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Exit(Market.Futures, ExecutionApp.Held(placed.Quantity)), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal([$"Closed {Q(placed.Quantity)} 59800 60400 Exited"], await app.PartsAsync(agent.Id));
        var close = Assert.Single(app.Book.SentTo(FakeOrderBook.FuturesOrder), sent => sent.Side == "SELL");
        Assert.Equal((placed.Quantity, true), (close.Quantity, close.ReduceOnly));
        Assert.Equal([close.ClientOrderId], result.ClientOrderIds);
        Assert.All(app.Book.Orders.Where(order => order.Algo), order => Assert.Equal("CANCELED", order.Status));
        Assert.Equal(0m, app.Book.FuturesAmount);
        Assert.Equal(["TradeOpened", "TradeClosed"], await app.NotificationsAsync(user.Id));
        Assert.Equal(1L, await app.CountAsync(agent.Id, "leg = 'Exit' AND origin = 'Council' AND status = 'Filled' AND system_evaluation IS NULL"));
    }

    [Fact]
    public async Task A_partial_Futures_close_splits_the_part_and_keeps_a_stop_for_the_rest()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        var exit = ExecutionApp.Exit(Market.Futures, ExecutionApp.Held(placed.Quantity), fraction: 0.5m);
        var rest = placed.Quantity - exit.Quantity!.Value;

        var result = await app.ExecuteAsync(app.Request(user, agent, exit, close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal(
            [$"Ended {Q(placed.Quantity)} 59800 60400 Split", $"Closed {Q(exit.Quantity.Value)} 59800 60400 Exited", $"Resting {Q(rest)} 59800 60400"],
            await app.PartsAsync(agent.Id));
        Assert.Equal(rest, app.Book.FuturesAmount);
        Assert.Equal("CANCELED", app.Book.Find(placed.Stop)!.Status);
        Assert.Equal(rest, Assert.Single(app.Book.Orders, order => order is { Type: "STOP_MARKET", Status: "NEW" }).Quantity);
        await ExecutionApp.UntilAsync(() => Task.FromResult(app.Book.Orders.Any(order => order is { Type: "TAKE_PROFIT_MARKET", Status: "NEW" } && order.Quantity == rest)));
    }

    [Fact]
    public async Task A_Spot_sell_cancels_the_OCO_list_first_and_sells_what_the_agents_fills_bought()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));
        app.Book.SpotBaseOwned = 1m;

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Exit(Market.Spot, ExecutionApp.Held(0.01664m)), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal(["Closed 0.01664 59800 60400 Exited"], await app.PartsAsync(agent.Id));
        Assert.All(app.Book.Orders.Where(order => order.ListId is not null), order => Assert.Equal("CANCELED", order.Status));
        var sell = Assert.Single(app.Book.SentTo(FakeOrderBook.SpotOrder), sent => sent.Side == "SELL");
        Assert.Equal((0.01664m, false), (sell.Quantity, sell.ReduceOnly));
    }

    [Fact]
    public async Task A_partial_Spot_sell_protects_the_rest_with_a_new_OCO_list()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));

        var result = await app.ExecuteAsync(
            app.Request(user, agent, ExecutionApp.Exit(Market.Spot, ExecutionApp.Held(0.01664m), fraction: 0.5m), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.Equal(
            ["Ended 0.01664 59800 60400 Split", "Closed 0.00832 59800 60400 Exited", "Resting 0.00832 59800 60400"],
            await app.PartsAsync(agent.Id));
        Assert.Equal(2, app.Book.SentTo(FakeOrderBook.SpotList).Count);
        Assert.Equal(0.00832m, Assert.Single(app.Book.Orders, order => order is { Type: "STOP_LOSS", Status: "NEW" }).Quantity);
    }

    [Fact]
    public async Task An_exit_whose_answer_is_lost_is_settled_by_its_client_order_ID_and_never_sent_again()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesOrder, Fault.ServerError(accepted: true));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Exit(Market.Futures, ExecutionApp.Held(placed.Quantity)), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        Assert.All(app.Book.Placed, sent => Assert.Equal(1, app.Book.SendsOf(sent.ClientOrderId)));
        Assert.Single(app.Book.SentTo(FakeOrderBook.FuturesOrder), sent => sent.Side == "SELL");
        Assert.Equal(0m, app.Book.FuturesAmount);
    }

    [Fact]
    public async Task An_exit_after_the_stop_executed_sends_nothing()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();
        app.Book.Trigger(placed.Stop);
        var sent = app.Book.Placed.Count;

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Exit(Market.Futures, ExecutionApp.Held(placed.Quantity)), close: app.LastClose.AddHours(1)));

        Assert.Equal((ExecutionStatus.NotSent, ExecutionReason.NoPosition), (result.Status, result.Reason));
        Assert.Equal(sent, app.Book.Placed.Count);
        Assert.Single(await app.PartsAsync(agent.Id), part => part.EndsWith("StopFilled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_exit_takes_the_oldest_part_first()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, target: false)));
        var first = app.FuturesLong().Quantity;
        app.Book.Price = 60_100m;
        var add = ExecutionApp.Add(Market.Futures, ExecutionApp.Held(first, target: null), price: 60_100m, stop: ExecutionApp.Stop);
        await app.ExecuteAsync(app.Request(user, agent, add, close: app.LastClose.AddHours(1)));

        var result = await app.ExecuteAsync(
            app.Request(user, agent, ExecutionApp.Exit(Market.Futures, ExecutionApp.Held(first, target: null), fraction: 1m) , close: app.LastClose.AddHours(2)));

        Assert.Equal(ExecutionStatus.Exited, result.Status);
        var parts = await app.PartsAsync(agent.Id);
        Assert.Equal($"Closed {Q(first)} 59800 - Exited", parts[0]);
        Assert.StartsWith("Resting", parts[1], StringComparison.Ordinal);
    }

    private static string Q(decimal value) => value.ToString("0.#############", System.Globalization.CultureInfo.InvariantCulture);
}
