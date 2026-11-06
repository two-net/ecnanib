using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// AC-14, NFR-REL-03, FR-EXE-05, by fault injection: after a timeout or a 5XX, an order is settled by its client order
/// ID, and nothing is sent again unless Binance has shown it was never placed, once it could no longer be accepted.
/// Entries are never sent again. That holds across a restart, which the adapter's memory doesn't survive.
/// </summary>
public sealed class IdempotencyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_entry_Binance_took_behind_a_server_error_is_found_and_protected_and_never_sent_again()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesOrder, Fault.ServerError(accepted: true));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        var entry = app.Book.SentTo(FakeOrderBook.FuturesOrder).Single();
        Assert.Equal(1, app.Book.SendsOf(entry.ClientOrderId));
        Assert.Equal(["STOP_MARKET", "TAKE_PROFIT_MARKET"], app.Book.SentTo(FakeOrderBook.FuturesAlgo).Select(sent => sent.Type));
    }

    [Fact]
    public async Task An_entry_Binance_never_took_is_not_sent_again_once_it_has_settled()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);

        // The answer is lost, and by the time anyone asks, the request could no longer be accepted.
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesOrder, Fault.ServerError(accepted: false, then: () => app.Clock.Advance(TimeSpan.FromSeconds(11))));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal(ExecutionStatus.NotPlaced, result.Status);
        Assert.False(result.Executed);
        Assert.Single(app.Book.Placed);
        Assert.Equal(["Entry Market Buy NotPlaced 0.0166"], await app.OrdersAsync(agent.Id));
    }

    [Fact]
    public async Task An_entry_whose_outcome_stays_unknown_is_never_sent_again_while_it_could_still_be_accepted()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesOrder, Fault.ServerError(accepted: false));

        // Every read fails too, so the cycle gives up waiting and leaves it to the watch.
        app.Book.Fail(HttpMethod.Get, FakeOrderBook.FuturesOrder, [.. Enumerable.Repeat(Fault.ServerError(accepted: false), 200)]);
        var request = app.Request(user, agent, ExecutionApp.Long(Market.Futures));
        var execution = app.ExecuteAsync(request);

        // The adapter's 10 seconds, and the cycle's own wait, pass.
        await ExecutionApp.UntilAsync(async () =>
        {
            app.Clock.Advance(TimeSpan.FromSeconds(5));
            return execution.IsCompleted;
        });

        Assert.Equal(ExecutionStatus.Unsettled, (await execution).Status);
        Assert.Single(app.Book.Placed);
        Assert.Equal(1L, await app.CountAsync(agent.Id, "status = 'Unknown'"));
    }

    [Fact]
    public async Task A_stop_Binance_took_behind_a_server_error_is_found_and_not_duplicated()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesAlgo, Fault.ServerError(accepted: true));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        var stop = app.Book.SentTo(FakeOrderBook.FuturesAlgo).First();
        Assert.Equal(1, app.Book.SendsOf(stop.ClientOrderId));
        Assert.Equal(["STOP_MARKET", "TAKE_PROFIT_MARKET"], app.Book.SentTo(FakeOrderBook.FuturesAlgo).Select(sent => sent.Type));
        Assert.Equal("NEW", app.Book.Find(stop.ClientOrderId)!.Status);
    }

    [Fact]
    public async Task A_stop_Binance_never_took_is_placed_under_a_new_ID_once_it_has_settled_within_its_window()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);

        // A protective order settles 7 seconds after it was signed, which leaves 2 of the 9 for another.
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.FuturesAlgo, Fault.ServerError(accepted: false, then: () => app.Clock.Advance(TimeSpan.FromSeconds(7.2))));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));

        Assert.Equal(ExecutionStatus.Protected, result.Status);
        var stops = app.Book.SentTo(FakeOrderBook.FuturesAlgo).Where(sent => sent.Type == "STOP_MARKET").ToList();
        Assert.Equal(2, stops.Count);
        Assert.NotEqual(stops[0].ClientOrderId, stops[1].ClientOrderId);
        Assert.All(app.Book.Placed, sent => Assert.Equal(1, app.Book.SendsOf(sent.ClientOrderId)));
        Assert.DoesNotContain(app.Book.Placed, sent => sent.Type == "MARKET" && sent.ReduceOnly);
    }

    [Fact]
    public async Task After_a_restart_an_order_left_being_sent_is_settled_by_its_ID_and_what_filled_is_protected()
    {
        var (connectionString, userId, agentId, clientOrderId) = await CrashedWhileSendingAsync();

        await using var restarted = new ExecutionApp(postgres, connectionString: connectionString);
        restarted.Book.Seed(clientOrderId, futures: false, "BUY", 0.01666m);

        await ExecutionApp.UntilAsync(async () => (await restarted.ProtectionsAsync(agentId)).SequenceEqual(["0 Resting 0.01664"]));

        Assert.Equal(0, restarted.Book.SendsOf(clientOrderId));
        Assert.Single(restarted.Book.SentTo(FakeOrderBook.SpotList));
        Assert.Equal(["TradeOpened"], await restarted.NotificationsAsync(userId));
    }

    [Fact]
    public async Task After_a_restart_an_order_Binance_never_took_is_not_sent_again_before_or_after_it_settles()
    {
        var (connectionString, _, agentId, clientOrderId) = await CrashedWhileSendingAsync();

        await using var restarted = new ExecutionApp(postgres, connectionString: connectionString);

        // The adapter has no memory of it, so only the row's own window holds it back.
        await ExecutionApp.UntilAsync(async () => await restarted.CountAsync(agentId, "status = 'Unknown'") == 1);
        Assert.Empty(restarted.Book.Placed);

        restarted.Clock.Advance(TimeSpan.FromMinutes(3));
        await ExecutionApp.UntilAsync(async () => await restarted.CountAsync(agentId, "status = 'NotPlaced' AND resolved") == 1);

        Assert.Empty(restarted.Book.Placed);
        Assert.Equal(0, restarted.Book.SendsOf(clientOrderId));
    }

    /// <summary>A Spot market buy written as being sent, as a crash between the write and the answer leaves it.</summary>
    private async Task<(string ConnectionString, Guid UserId, Guid AgentId, string ClientOrderId)> CrashedWhileSendingAsync()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        var clientOrderId = $"eCrash{Guid.NewGuid():N}"[..30];

        await Sql.ExecuteAsync(
            app.ConnectionString,
            $"""
            INSERT INTO orders (id, user_id, agent_id, cycle_id, origin, candle_close, credential_id, market, environment, symbol, leg,
                client_order_id, type, side, quantity, reduce_only, protective_stop, target, status, executed_quantity, resolved,
                settles_at, created_at, updated_at)
            VALUES (gen_random_uuid(), '{user.Id}', '{agent.Id}', gen_random_uuid(), 'Council', now(), '{user.ExchangeKey}', 'Spot',
                'Demo', 'BTCUSDT', 'Entry', '{clientOrderId}', 'Market', 'Buy', 0.01666, false, {ExecutionApp.Stop}, {ExecutionApp.Target},
                'Sending', 0, false, now() + interval '2 minutes', now(), now())
            """,
            ExecutionApp.Cancellation);

        return (app.ConnectionString, user.Id, agent.Id, clientOrderId);
    }
}
