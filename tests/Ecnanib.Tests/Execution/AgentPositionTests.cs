using Ecnanib.Kernel;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// What a cycle's account read (FR-CYC-02 step 2) gives the Risk Engine and the prompts, once the agent's orders are
/// reconciled (FR-EXE-06): the agent's own position from its records, with its stop, targets and opening time, Binance's
/// break-even and liquidation prices when Binance's position is the agent's own, and the entry that hasn't filled.
/// </summary>
public sealed class AgentPositionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_Spot_position_is_what_the_agents_fills_bought_with_its_stop_target_and_opening_time()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        var before = app.Clock.GetUtcNow();
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Spot)));

        var account = await ReadAsync(app, user, agent.Id, Market.Spot);

        Assert.Equal(new OpenPosition(PositionSide.Long, 0.01664m, ExecutionApp.Price, null, ExecutionApp.Stop, ExecutionApp.Target), account.Position);
        Assert.Equal([ExecutionApp.Target], account.Targets);
        Assert.InRange(account.PositionOpenedAt!.Value, before.AddSeconds(-1), app.Clock.GetUtcNow());
        Assert.Null(account.LiquidationPrice);
        Assert.Null(account.PendingEntry);
    }

    [Fact]
    public async Task A_Futures_position_takes_Binances_break_even_and_liquidation_prices_only_when_Binances_position_is_the_agents()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        app.Book.LiquidationPrice = 50_000m;

        var own = await ReadAsync(app, user, agent.Id, Market.Futures);

        Assert.Equal((ExecutionApp.Price, 50_000m), (own.Position!.BreakEven, own.LiquidationPrice));

        app.Book.FuturesPositionOwned = 0.01m;
        var shared = await ReadAsync(app, user, agent.Id, Market.Futures);

        Assert.Equal(own.Position.Quantity, shared.Position!.Quantity);
        Assert.Equal((null, null), (shared.Position.BreakEven, shared.LiquidationPrice));
    }

    [Fact]
    public async Task An_added_position_averages_its_parts_entries_and_has_one_stop()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures, target: false)));
        var first = app.FuturesLong().Quantity;
        app.Book.Price = 60_100m;
        var add = ExecutionApp.Add(Market.Futures, ExecutionApp.Held(first, target: null), price: 60_100m, stop: 59_900m);
        await app.ExecuteAsync(app.Request(user, agent, add, close: app.LastClose.AddHours(1)));
        var second = add.Quantity!.Value;

        var account = await ReadAsync(app, user, agent.Id, Market.Futures);

        Assert.Equal(first + second, account.Position!.Quantity);
        Assert.Equal(Math.Round(((first * 60_000m) + (second * 60_100m)) / (first + second), 8), account.Position.AverageEntry);
        Assert.Equal(59_900m, account.Position.Stop);
        Assert.Empty(account.Targets);
    }

    [Fact]
    public async Task An_unfilled_limit_entry_is_the_pending_entry_with_its_price_and_when_it_was_placed()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync();
        var order = ExecutionApp.Long(Market.Spot, EntryType.Limit, 59_900m);
        await app.ExecuteAsync(app.Request(user, agent, order, ttl: 3));

        var account = await ReadAsync(app, user, agent.Id, Market.Spot);

        var pending = account.PendingEntry!;
        Assert.Equal((PositionSide.Long, EntryType.Limit, 59_900m, order.Quantity!.Value, 0m), (pending.Side, pending.Type, pending.Price, pending.Quantity, pending.Executed));
        Assert.True(account.HasPendingEntry);
        Assert.Null(account.Position);
    }

    [Fact]
    public async Task A_cycles_read_reconciles_first_so_a_stop_that_executed_leaves_no_position()
    {
        await using var app = new ExecutionApp(postgres);
        var (user, agent) = await app.AgentAsync(Market.Futures);
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        app.Book.Trigger(app.FuturesLong().Stop);

        var account = await ReadAsync(app, user, agent.Id, Market.Futures);

        Assert.Null(account.Position);
        Assert.Single(await app.PartsAsync(agent.Id), part => part.EndsWith("StopFilled", StringComparison.Ordinal));
        Assert.Equal("CANCELED", app.Book.Find(app.FuturesLong().Target)!.Status);
    }

    private static Task<AgentAccount> ReadAsync(ExecutionApp app, AgentApp.User user, Guid agentId, Market market) =>
        app.Agents.App.Services.GetRequiredService<IAgentAccounts>()
            .ReadAsync(user.Id, agentId, user.ExchangeKey, market, "BTCUSDT", "BTC", "USDT", ExecutionApp.Cancellation);
}
