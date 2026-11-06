using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// When reconciliation runs. FR-EXE-06: on an interval, for every agent with an open position or order whatever its
/// status, so a paused agent's is too (FR-AGT-06), and one agent failing doesn't stop the rest (NFR-REL-05). FR-EXE-08:
/// on startup, finished before the application has, and so before any scheduled cycle.
/// </summary>
public sealed class ReconcileScheduleTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_paused_agents_stop_that_executed_is_recorded_and_notified_by_the_sweep()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        await Sql.ExecuteAsync(app.ConnectionString, $"UPDATE agents SET status = 'Paused' WHERE id = '{agent.Id}'", ExecutionApp.Cancellation);
        var stop = app.Book.Orders.Single(order => order.Type == "STOP_MARKET");

        app.Book.Trigger(stop.ClientOrderId);
        Assert.Equal(JobStatus.Succeeded, await app.Agents.RunJobAsync("ReconcileSweep"));

        Assert.Single(await app.PartsAsync(agent.Id), part => part.EndsWith("StopFilled", StringComparison.Ordinal));
        Assert.Equal(["TradeOpened", "TradeClosed"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task An_agent_that_cant_be_reconciled_doesnt_stop_the_others()
    {
        await using var app = new ExecutionApp(postgres);
        var (spotUser, spot) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(spotUser, spot, ExecutionApp.Long(Market.Spot)));
        var (futuresUser, futures) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(futuresUser, futures, ExecutionApp.Long(Market.Futures)));

        // The Spot agent's key is gone, as if its credential had changed since it traded.
        await Sql.ExecuteAsync(
            app.ConnectionString, $"UPDATE orders SET credential_id = gen_random_uuid() WHERE agent_id = '{spot.Id}'", ExecutionApp.Cancellation);
        app.Book.Trigger(app.Book.Orders.Single(order => order.Type == "STOP_LOSS").ClientOrderId);
        app.Book.Trigger(app.Book.Orders.Single(order => order.Type == "STOP_MARKET").ClientOrderId);

        Assert.Equal(JobStatus.Succeeded, await app.Agents.RunJobAsync("ReconcileSweep"));

        Assert.Equal(["Resting 0.01664 59800 60400"], await app.PartsAsync(spot.Id));
        Assert.Single(await app.PartsAsync(futures.Id), part => part.EndsWith("StopFilled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stop_that_executed_while_Ecnanib_was_down_is_recorded_before_startup_finishes()
    {
        string connectionString;
        Guid userId;
        Guid agentId;
        ExecutionApp.Placed placed;

        await using (var app = new ExecutionApp(postgres))
        {
            var (user, agent) = await app.AgentAsync(Market.Futures);
            Assert.Equal(ExecutionStatus.Protected, (await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)))).Status);
            (connectionString, userId, agentId, placed) = (app.ConnectionString, user.Id, agent.Id, app.FuturesLong());
        }

        // Binance filled the stop while Ecnanib was down. The take-profit still rests.
        await using var restarted = new ExecutionApp(
            postgres,
            connectionString: connectionString,
            prepare: book =>
            {
                book.Seed(placed.Entry, futures: true, "BUY", placed.Quantity);
                book.SeedAlgo(placed.Stop, "SELL", "STOP_MARKET", placed.Quantity, ExecutionApp.Stop, finished: true);
                book.SeedAlgo(placed.Target, "SELL", "TAKE_PROFIT_MARKET", placed.Quantity, ExecutionApp.Target, finished: false);
            });

        // No waiting: the application has only just started.
        Assert.Single(await restarted.PartsAsync(agentId), part => part.EndsWith("StopFilled", StringComparison.Ordinal));
        Assert.Equal("CANCELED", restarted.Book.Find(placed.Target)!.Status);
        Assert.Equal(1L, await restarted.Agents.FinishedAsync("reconcile-startup"));
        Assert.Equal(["TradeOpened", "TradeClosed"], await restarted.NotificationsAsync(userId));
    }

    [Fact]
    public async Task Startup_records_no_reconciliation_when_no_agent_has_anything_on_Binance()
    {
        await using var app = new ExecutionApp(postgres);
        await app.AgentAsync(Market.Futures);

        Assert.Equal(0L, await app.Agents.FinishedAsync("reconcile-startup"));
    }
}
