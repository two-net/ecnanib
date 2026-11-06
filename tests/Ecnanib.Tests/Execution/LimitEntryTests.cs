using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// FR-EXE-01, FR-EXE-02, AC-02: a limit entry rests until it fills or its time-to-live in candles ends. The agent's
/// watch checks it, and each part that fills gets a stop of its own within seconds. CANCEL_PENDING cancels only the
/// entry, never a stop.
/// </summary>
public sealed class LimitEntryTests(PostgresFixture postgres)
{
    private const decimal Limit = 59_900m;

    private static Dictionary<string, string?> Settings => new() { ["Agents:PendingEntryCheckSeconds"] = "1" };

    [Fact]
    public async Task A_limit_entry_rests_until_it_fills_and_what_fills_rests_behind_a_stop_within_seconds()
    {
        await using var app = new ExecutionApp(postgres, Settings);
        var (user, agent) = await app.AgentAsync();

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot, EntryType.Limit, Limit), ttl: 3));

        Assert.Equal(ExecutionStatus.Placed, result.Status);
        var entry = app.Book.Placed.Single();
        Assert.Equal(("LIMIT", "NEW"), (entry.Type, app.Book.Find(entry.ClientOrderId)!.Status));

        app.Book.Fill(entry.ClientOrderId);
        var filled = app.Clock.GetUtcNow();

        await ExecutionApp.UntilAsync(async () => (await app.ProtectionsAsync(agent.Id)).Count == 1 && app.Book.SentTo(FakeOrderBook.SpotList).Count == 1);
        await ExecutionApp.UntilAsync(async () => (await app.ProtectionsAsync(agent.Id))[0].Contains("Resting", StringComparison.Ordinal));

        Assert.InRange((app.Book.SentTo(FakeOrderBook.SpotList)[0].At - filled).TotalSeconds, 0, 10);
        Assert.Equal(1, app.Book.SendsOf(entry.ClientOrderId));
        Assert.Equal(["TradeOpened"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task A_limit_entry_that_doesnt_fill_is_cancelled_when_its_candles_are_up()
    {
        await using var app = new ExecutionApp(postgres, Settings);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        var request = app.Request(user, agent, ExecutionApp.Long(Market.Futures, EntryType.Limit, Limit), ttl: 2);

        Assert.Equal(ExecutionStatus.Placed, (await app.ExecuteAsync(request)).Status);

        var expires = await Sql.ScalarAsync<DateTime>(
            app.ConnectionString, $"SELECT expires_at FROM orders WHERE agent_id = '{agent.Id}'", ExecutionApp.Cancellation);
        Assert.Equal(request.CandleClose.UtcDateTime + TimeSpan.FromHours(2), expires);

        app.Clock.Advance(TimeSpan.FromHours(2));
        var entry = app.Book.Placed.Single();
        await ExecutionApp.UntilAsync(() => Task.FromResult(app.Book.Find(entry.ClientOrderId)!.Status == "CANCELED"));
        await ExecutionApp.UntilAsync(async () => await app.CountAsync(agent.Id, "status = 'Canceled' AND resolved") == 1);

        Assert.Single(app.Book.Placed);
        Assert.Empty(await app.ProtectionsAsync(agent.Id));
    }

    [Fact]
    public async Task A_time_to_live_is_brought_down_to_the_most_allowed()
    {
        await using var app = new ExecutionApp(postgres, new() { ["Agents:MaxLimitTtlCandles"] = "4" });
        var (user, agent) = await app.AgentAsync();
        var request = app.Request(user, agent, ExecutionApp.Long(Market.Spot, EntryType.Limit, Limit), ttl: 1000);

        await app.ExecuteAsync(request);

        var expires = await Sql.ScalarAsync<DateTime>(
            app.ConnectionString, $"SELECT expires_at FROM orders WHERE agent_id = '{agent.Id}'", ExecutionApp.Cancellation);
        Assert.Equal(request.CandleClose.UtcDateTime + TimeSpan.FromHours(4), expires);
    }

    [Fact]
    public async Task A_limit_entry_partly_filled_when_its_time_is_up_keeps_a_stop_for_what_filled()
    {
        await using var app = new ExecutionApp(postgres, Settings);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, EntryType.Limit, Limit), ttl: 1));
        var entry = app.Book.Placed.Single();

        app.Book.Fill(entry.ClientOrderId, 0.006m);
        await ExecutionApp.UntilAsync(async () => (await app.ProtectionsAsync(agent.Id)).SequenceEqual(["0 Resting 0.006"]));

        app.Clock.Advance(TimeSpan.FromHours(1));
        await ExecutionApp.UntilAsync(async () => await app.CountAsync(agent.Id, "leg = 'Entry' AND status = 'Canceled' AND resolved") == 1);

        var stops = app.Book.SentTo(FakeOrderBook.FuturesAlgo).Where(sent => sent.Type == "STOP_MARKET").ToList();
        Assert.Equal([0.006m], stops.Select(stop => stop.Quantity));
        Assert.Equal("NEW", app.Book.Find(stops[0].ClientOrderId)!.Status);
    }

    [Fact]
    public async Task Each_part_of_a_limit_entry_that_fills_gets_a_stop_of_its_own()
    {
        await using var app = new ExecutionApp(postgres, Settings);
        var (user, agent) = await app.AgentAsync();
        var order = ExecutionApp.Long(Market.Spot, EntryType.Limit, Limit);
        await app.ExecuteAsync(app.Request(user, agent, order, ttl: 3));
        var entry = app.Book.Placed.Single();

        app.Book.Fill(entry.ClientOrderId, 0.01m);
        await ExecutionApp.UntilAsync(async () => (await app.ProtectionsAsync(agent.Id)).SequenceEqual(["0 Resting 0.00999"]));

        app.Book.Fill(entry.ClientOrderId);
        await ExecutionApp.UntilAsync(async () => (await app.ProtectionsAsync(agent.Id)).Count == 2
            && (await app.ProtectionsAsync(agent.Id)).All(protection => protection.Contains("Resting", StringComparison.Ordinal)));

        // Each list sells only what its part left after the fee, and every ID is new.
        var lists = app.Book.SentTo(FakeOrderBook.SpotList);
        Assert.Equal(2, lists.Count);
        Assert.Equal(Math.Floor(order.Quantity!.Value * 0.999m * 100_000m) / 100_000m, lists.Sum(list => list.Quantity));
        Assert.Equal(app.Book.Placed.Count, app.Book.Placed.Select(sent => sent.ClientOrderId).Distinct().Count());
        Assert.Equal(["TradeOpened"], await app.NotificationsAsync(user.Id));
    }

    [Fact]
    public async Task CANCEL_PENDING_cancels_only_the_unfilled_entry_and_never_a_stop()
    {
        await using var app = new ExecutionApp(postgres, Settings);
        var (user, agent) = await app.AgentAsync();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot, EntryType.Limit, Limit), ttl: 3));
        var entry = app.Book.Placed.Single();
        app.Book.Fill(entry.ClientOrderId, 0.01m);
        await ExecutionApp.UntilAsync(async () => (await app.ProtectionsAsync(agent.Id)).SequenceEqual(["0 Resting 0.00999"]));

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.CancelPending(Market.Spot), close: app.LastClose.AddHours(1)));

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal("CANCELED", app.Book.Find(entry.ClientOrderId)!.Status);
        Assert.All(app.Book.Orders.Where(order => order.ListId is not null), order => Assert.Equal("NEW", order.Status));
    }

    [Fact]
    public async Task CANCEL_PENDING_with_no_unfilled_entry_sends_nothing()
    {
        await using var app = new ExecutionApp(postgres, Settings);
        var (user, agent) = await app.AgentAsync();

        var result = await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.CancelPending(Market.Spot)));

        Assert.Equal((ExecutionStatus.NotSent, ExecutionReason.NothingToCancel), (result.Status, result.Reason));
        Assert.Empty(app.Book.Placed);
    }
}
