using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// What execution records answers the rest of the application without calling Binance: the pending entry for the Risk
/// Engine, open positions and orders for FR-AGT-03's lock, and an agent's history, which goes when it's deleted.
/// </summary>
public sealed class ExecutionRecordTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_unfilled_entry_is_the_agents_pending_entry_until_it_ends()
    {
        await using var app = new ExecutionApp(postgres, new() { ["Agents:PendingEntryCheckSeconds"] = "1" });
        var (user, agent) = await app.AgentAsync();
        var accounts = app.Agents.App.Services.GetRequiredService<IAgentAccounts>();

        Task<AgentAccount> ReadAsync() => accounts.ReadAsync(user.Id, agent.Id, user.ExchangeKey, Market.Spot, "BTCUSDT", "BTC", "USDT", ExecutionApp.Cancellation);

        Assert.False((await ReadAsync()).HasPendingEntry);

        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot, EntryType.Limit, 59_900m), ttl: 1));
        Assert.True((await ReadAsync()).HasPendingEntry);

        app.Clock.Advance(TimeSpan.FromHours(1));
        await ExecutionApp.UntilAsync(async () => !(await ReadAsync()).HasPendingEntry);
    }

    [Fact]
    public async Task A_resting_stop_is_an_open_order_and_an_entry_never_placed_isnt()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        var (otherUser, other) = await app.AgentAsync();
        var exposure = app.Agents.App.Services.GetRequiredService<IAgentExposure>();
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.SpotOrder, null, Fault.Rejected(-2010, "Account has insufficient balance for requested action."));

        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));
        await app.ExecuteAsync(app.Request(otherUser, other, ExecutionApp.Long(Market.Spot)));

        Assert.True(await exposure.HasOpenPositionsOrOrdersAsync(user.Id, agent.Id, ExecutionApp.Cancellation));
        Assert.False(await exposure.HasOpenPositionsOrOrdersAsync(otherUser.Id, other.Id, ExecutionApp.Cancellation));

        // Another user never sees it (NFR-SEC-04).
        Assert.False(await exposure.HasOpenPositionsOrOrdersAsync(otherUser.Id, agent.Id, ExecutionApp.Cancellation));

        // Agents won't archive an agent with a resting stop.
        Assert.Equal(TransitionOutcome.OpenPositions, (await app.Agents.Agents.ArchiveAsync(user.Id, agent.Id, ExecutionApp.Cancellation)).Outcome);
    }

    [Fact]
    public async Task Deleting_an_agent_deletes_its_orders()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        app.Book.Fail(HttpMethod.Post, FakeOrderBook.SpotOrder, Fault.Rejected(-2010, "Account has insufficient balance for requested action."));
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));
        Assert.Equal(1L, await app.CountAsync(agent.Id, "true"));

        Assert.Equal(TransitionOutcome.Done, (await app.Agents.Agents.ArchiveAsync(user.Id, agent.Id, ExecutionApp.Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.Agents.DeleteAsync(user.Id, agent.Id, ExecutionApp.Cancellation)).Outcome);

        Assert.Equal(0L, await app.CountAsync(agent.Id, "true"));
    }
}
