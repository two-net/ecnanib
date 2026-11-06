using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// AC-02, FR-EXE-02, NFR-REL-02, by fault injection: every filled entry has an exchange-resting stop within 10 seconds,
/// or what filled is closed at market, after an evaluation of its own (AC-01), and the user is told. A target Binance
/// refuses never closes anything.
/// </summary>
public sealed class ProtectionTests(PostgresFixture postgres)
{
    /// <summary>The shortest window, so a stop Binance keeps refusing is given up on sooner.</summary>
    private static Dictionary<string, string?> Short => new()
    {
        ["Agents:PendingEntryCheckSeconds"] = "1",
        ["Agents:ProtectiveStopSeconds"] = "5",
    };

    [Fact]
    public async Task A_Futures_position_whose_stop_Binance_keeps_refusing_is_closed_reduce_only_within_ten_seconds()
    {
        await using var app = new ExecutionApp(postgres, Short);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesAlgo, [.. Enumerable.Repeat(Fault.Rejected(-1102, "Mandatory parameter was not sent."), 50)]);

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal(ExecutionStatus.Closed, result.Status);
        var orders = app.Book.SentTo(FakeOrderBook.FuturesOrder);
        Assert.Equal([("MARKET", "BUY", false), ("MARKET", "SELL", true)], orders.Select(sent => (sent.Type, sent.Side, sent.ReduceOnly)));
        Assert.Equal(orders[0].Quantity, orders[1].Quantity);
        Assert.InRange((orders[1].At - orders[0].At).TotalSeconds, 4, 10);
        Assert.Equal(0m, app.Book.FuturesAmount);

        Assert.Equal(["0 Closed 0.0166"], await app.ProtectionsAsync(agent.Id));
        Assert.Equal(["TradeClosed"], await app.NotificationsAsync(user.Id));
        Assert.Equal("Pass", await SystemVerdictAsync(app, agent.Id));
    }

    [Fact]
    public async Task A_Spot_position_whose_stop_would_trigger_at_once_is_sold_at_once_for_what_its_fills_left()
    {
        await using var app = new ExecutionApp(postgres, Short);
        var (user, agent) = await app.AgentAsync();
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.SpotList, Fault.Rejected(-2010, "Order would trigger immediately."));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));

        Assert.Equal(ExecutionStatus.Closed, result.Status);
        var orders = app.Book.SentTo(FakeOrderBook.SpotOrder);
        Assert.Equal([("MARKET", "BUY"), ("MARKET", "SELL")], orders.Select(sent => (sent.Type, sent.Side)));

        // Spot has no reduce-only, and the agent sells only what its own fills left (FR-AGT-09).
        Assert.False(orders[1].ReduceOnly);
        Assert.Equal(0.01664m, orders[1].Quantity);
        Assert.InRange((orders[1].At - orders[0].At).TotalSeconds, 0, 3);
        Assert.Single(app.Book.SentTo(FakeOrderBook.SpotList));
        Assert.Equal(["TradeClosed"], await app.NotificationsAsync(user.Id));
        Assert.Equal("Pass", await SystemVerdictAsync(app, agent.Id));
    }

    [Fact]
    public async Task A_Futures_stop_still_unknown_at_its_deadline_closes_the_position_without_waiting_and_is_cancelled_once_found()
    {
        await using var app = new ExecutionApp(postgres, Short);
        var (user, agent) = await app.AgentAsync(Market.Futures);

        // Binance took the stop, but no read of it works for a while.
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesAlgo, Fault.ServerError(accepted: true));
        app.Book.Fail(HttpMethod.Get, FakeOrderBook.FuturesAlgo, [.. Enumerable.Repeat(Fault.ServerError(accepted: false), 12)]);

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal(ExecutionStatus.Closed, result.Status);
        var close = app.Book.SentTo(FakeOrderBook.FuturesOrder)[1];
        Assert.True(close.ReduceOnly);
        Assert.InRange((close.At - app.Book.Placed[0].At).TotalSeconds, 4, 10);

        // The watch finds the stop once reads work again, and cancels it: it protects nothing now.
        var stop = app.Book.SentTo(FakeOrderBook.FuturesAlgo).Single();
        await ExecutionApp.UntilAsync(() => Task.FromResult(app.Book.Find(stop.ClientOrderId)!.Status == "CANCELED"));
        Assert.Equal(1, app.Book.SendsOf(stop.ClientOrderId));
    }

    [Fact]
    public async Task A_Spot_target_Binance_refuses_leaves_a_plain_stop_and_closes_nothing()
    {
        await using var app = new ExecutionApp(postgres, Short);
        var (user, agent) = await app.AgentAsync();
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.SpotList, Fault.Rejected(-2010, "Order would immediately match and take."));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        Assert.True(result.TargetNotPlaced);
        Assert.Equal(["MARKET", "OCO", "STOP_LOSS"], app.Book.Placed.Select(sent => sent.Type));
        Assert.Equal(["TradeOpened"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_Futures_target_Binance_refuses_is_dropped_and_its_stop_rests()
    {
        await using var app = new ExecutionApp(postgres, Short);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesAlgo, null, Fault.Rejected(-2021, "Order would immediately trigger."));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        Assert.True(result.TargetNotPlaced);
        Assert.Equal(["MARKET", "STOP_MARKET", "TAKE_PROFIT_MARKET"], app.Book.Placed.Select(sent => sent.Type));
        Assert.Equal(["0 Resting 0.0166"], await app.ProtectionsAsync(agent.Id));
    }

    private static async Task<string?> SystemVerdictAsync(ExecutionApp app, Guid agentId)
    {
        var evaluations = await Sql.ColumnAsync(
            app.ConnectionString,
            $"SELECT system_evaluation::text FROM orders WHERE agent_id = '{agentId}' AND origin = 'System'",
            ExecutionApp.Cancellation);

        using var evaluation = JsonDocument.Parse(Assert.Single(evaluations));

        return evaluation.RootElement.GetProperty("verdict").GetString();
    }
}
