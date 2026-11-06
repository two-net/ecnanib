using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// FR-EXE-01 to FR-EXE-04: an entry is sent once, and what fills gets an exchange-resting stop at once, with its target
/// when it has one: a Spot OCO list, or a Futures reduce-only stop and take-profit on the mark price.
/// </summary>
public sealed class OrderExecutionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_Spot_market_entry_is_protected_by_an_OCO_list_for_what_its_fills_left_after_the_fee()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        var order = ExecutionApp.Long(Market.Spot);

        var result = await app.ExecuteAsync(app.Request(user, agent, order));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        Assert.True(result.Executed);

        var entry = app.Book.Placed[0];
        var list = Assert.Single(app.Book.Placed, sent => sent.Path == FakeOrderBook.SpotList);
        Assert.Equal(("MARKET", "BUY", order.Quantity!.Value), (entry.Type, entry.Side, entry.Quantity));

        // Binance took 0.1% of the BTC bought, so the stop sells what's left, on the 0.00001 step.
        Assert.Equal(Math.Floor(order.Quantity.Value * 0.999m * 100_000m) / 100_000m, list.Quantity);
        Assert.Equal((ExecutionApp.Stop, ExecutionApp.Target, "SELL", "2000"), (list.StopPrice, list.Price, list.Side, list.RecvWindow));
        Assert.Equal("5000", entry.RecvWindow);

        Assert.Equal(
            [
                $"Entry Market Buy Filled {N(order.Quantity.Value)}",
                $"Stop StopMarket Sell Open {N(list.Quantity)}",
                $"Target LimitMaker Sell Open {N(list.Quantity)}",
            ],
            await app.OrdersAsync(agent.Id));
        Assert.Equal([$"0 Resting {N(list.Quantity)}"], await app.ProtectionsAsync(agent.Id));
        Assert.Equal(["TradeOpened"], await app.NotificationsAsync(user.Id));
        Assert.Equal(result.ClientOrderIds, [entry.ClientOrderId, list.ClientOrderId]);
    }

    [Fact]
    public async Task A_Futures_market_entry_is_protected_by_a_reduce_only_stop_and_take_profit_on_the_mark_price()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        var order = ExecutionApp.Long(Market.Futures);

        var result = await app.ExecuteAsync(app.Request(user, agent, order));

        Assert.Equal(ExecutionStatus.Protected, result.Status);

        var placed = app.Book.Placed;
        Assert.Equal(["MARKET", "STOP_MARKET", "TAKE_PROFIT_MARKET"], placed.Select(sent => sent.Type));
        Assert.Equal([false, true, true], placed.Select(sent => sent.ReduceOnly));
        Assert.Equal([null, "MARK_PRICE", "MARK_PRICE"], placed.Select(sent => sent.WorkingType));
        Assert.Equal([order.Quantity!.Value, order.Quantity.Value, order.Quantity.Value], placed.Select(sent => sent.Quantity));
        Assert.Equal((ExecutionApp.Stop, ExecutionApp.Target), (placed[1].StopPrice, placed[2].StopPrice));
        Assert.Equal(["TradeOpened"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_market_entry_Binance_answered_before_it_filled_is_read_again_at_once_and_protected()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesOrder, Fault.AnsweredBeforeTheFill());

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        var stop = app.Book.SentTo(FakeOrderBook.FuturesAlgo)[0];
        Assert.InRange((stop.At - app.Book.Placed[0].At).TotalSeconds, 0, 2);
    }

    [Fact]
    public async Task A_Futures_short_is_protected_by_buying_above_and_below()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Short()));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        Assert.Equal(["SELL", "BUY", "BUY"], app.Book.Placed.Select(sent => sent.Side));
        Assert.Equal([null, 60_200m, 59_600m], app.Book.Placed.Select(sent => sent.StopPrice));
    }

    [Fact]
    public async Task A_Spot_entry_without_a_target_is_protected_by_a_plain_stop()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot, target: false)));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        Assert.Equal(["MARKET", "STOP_LOSS"], app.Book.Placed.Select(sent => sent.Type));
        Assert.False(result.TargetNotPlaced);
    }

    /// <summary>As PostgreSQL's trim_scale writes it.</summary>
    internal static string N(decimal value) => value.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
}
