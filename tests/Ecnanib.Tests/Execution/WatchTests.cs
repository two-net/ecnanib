using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// NFR-REL-02, NFR-REL-04: the sweep starts a watch for any agent whose orders nobody is watching, every minute as well
/// as at startup, and a protection under way when shutdown begins is seen through, so no fill is left half-recorded.
/// </summary>
public sealed class WatchTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_sweep_watches_an_agent_whose_orders_nobody_is_watching()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        var clientOrderId = $"eSweep{Guid.NewGuid():N}"[..30];
        app.Book.Seed(clientOrderId, futures: false, "BUY", 0.01666m);

        // As if a watch had been lost: an entry whose outcome nobody has settled.
        await Sql.ExecuteAsync(
            app.ConnectionString,
            $"""
            INSERT INTO orders (id, user_id, agent_id, cycle_id, origin, candle_close, credential_id, market, environment, symbol, leg,
                client_order_id, type, side, quantity, reduce_only, protective_stop, target, status, executed_quantity, resolved,
                settles_at, created_at, updated_at, sent_at)
            VALUES (gen_random_uuid(), '{user.Id}', '{agent.Id}', gen_random_uuid(), 'Council', now(), '{user.ExchangeKey}', 'Spot',
                'Demo', 'BTCUSDT', 'Entry', '{clientOrderId}', 'Market', 'Buy', 0.01666, false, {ExecutionApp.Stop}, {ExecutionApp.Target},
                'Unknown', 0, false, now() + interval '10 seconds', now(), now(), now())
            """,
            ExecutionApp.Cancellation);

        Assert.Equal(Platform.Contracts.JobStatus.Succeeded, await app.Agents.RunJobAsync("OrderWatchSweep"));

        await ExecutionApp.UntilAsync(async () => (await app.ProtectionsAsync(agent.Id)).SequenceEqual(["0 Resting 0.01664"]));
        Assert.Equal(0, app.Book.SendsOf(clientOrderId));
    }

    [Fact]
    public async Task A_stop_being_placed_when_shutdown_begins_is_seen_through_and_recorded()
    {
        var app = new ExecutionApp(postgres, new() { ["Agents:PendingEntryCheckSeconds"] = "1" });
        var database = app.ConnectionString;
        Guid agentId;

        try
        {
            var (user, agent) = await app.AgentAsync();
            agentId = agent.Id;
            await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot, EntryType.Limit, 59_900m), ttl: 3));

            // The watch finds the fill and starts placing its stop, which Binance takes but answers slowly.
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Book.Fail(HttpMethod.Post, FakeOrderBook.SpotList, Fault.Slow(TimeSpan.FromSeconds(2), arrived.SetResult));
            app.Book.Fill(app.Book.Placed.Single().ClientOrderId);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(30), ExecutionApp.Cancellation);
        }
        finally
        {
            await app.DisposeAsync();
        }

        var protections = await Sql.ColumnAsync(
            database,
            $"SELECT status FROM protections WHERE agent_id = '{agentId}'",
            ExecutionApp.Cancellation);
        Assert.Equal(["Resting"], protections);
        Assert.Equal(2L, await Sql.ScalarAsync<long>(
            database,
            $"SELECT count(*) FROM orders WHERE agent_id = '{agentId}' AND list_client_order_id IS NOT NULL AND status = 'Open'",
            ExecutionApp.Cancellation));
    }
}
