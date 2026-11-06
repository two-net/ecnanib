using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Execution;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// FR-RISK-02 and FR-RISK-08 with the trade journal (#36): each cycle counts the agent's realized P&amp;L today and this
/// week, its losses in a row since it last became Active, and the cycles since its last loss, from its closed trades. A
/// limit reached pauses the agent, and the cooldown holds back the next entry.
/// </summary>
public sealed class TradingRecordTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task Three_losing_trades_in_a_row_since_the_agent_became_Active_pause_it_until_its_user_resumes_it()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        for (var trade = 0; trade < 3; trade++)
        {
            await ClosedAsync(app, user, agent, -0.1m, app.Clock.GetUtcNow());
        }

        await app.RunAsync(user, agent);

        Assert.Equal(["PausedForLossLimit"], await app.CyclesAsync(agent.Id));
        var paused = await app.Agents.Agents.FindAsync(user.Id, agent.Id, Cancellation);
        Assert.Equal((AgentStatus.Paused, AgentStatusReason.ConsecutiveLosses, (DateTimeOffset?)null), (paused!.Status, paused.StatusReason, paused.LossLimitUntil));
    }

    [Fact]
    public async Task A_loss_that_reaches_the_daily_limit_pauses_the_agent_until_the_next_UTC_day()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        app.Clock.Advance(TimeSpan.FromMinutes(1));

        // 3% of the capital of 100.
        await ClosedAsync(app, user, agent, -3m, app.Clock.GetUtcNow());
        await app.RunAsync(user, agent);

        Assert.Equal(["PausedForLossLimit"], await app.CyclesAsync(agent.Id));
        var paused = await app.Agents.Agents.FindAsync(user.Id, agent.Id, Cancellation);
        var now = app.Clock.GetUtcNow();
        Assert.Equal(
            (AgentStatus.Paused, AgentStatusReason.DailyLossLimit, new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1)),
            (paused!.Status, paused.StatusReason, paused.LossLimitUntil!.Value));
    }

    [Fact]
    public async Task A_resumed_agents_streak_starts_again_and_the_cooldown_counts_the_cycles_started_after_the_loss()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();

        // Losses from before the agent last became Active, as before a resume.
        for (var trade = 0; trade < 3; trade++)
        {
            await ClosedAsync(app, user, agent, -0.1m, app.Clock.GetUtcNow().AddMinutes(-30));
        }

        await app.RunAsync(user, agent);

        Assert.Equal(["BlockedByRiskEngine"], await app.CyclesAsync(agent.Id));
        var prompt = Assert.Single(await app.CallColumnAsync(agent.Id, AiRole.Strategist, "message"));
        Assert.Contains("realized_pnl_today: -0.3\n", prompt, StringComparison.Ordinal);
        Assert.Contains("consecutive_losses: 0\n", prompt, StringComparison.Ordinal);
        Assert.Contains("cycles_since_last_loss: 0\n", prompt, StringComparison.Ordinal);

        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.RunAsync(user, agent);

        Assert.Equal(["ReadyToExecute", "BlockedByRiskEngine"], await app.CyclesAsync(agent.Id));
        Assert.Contains("cycles_since_last_loss: 1\n", (await app.CallColumnAsync(agent.Id, AiRole.Strategist, "message"))[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Losses_from_before_this_week_trip_no_limit()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with { Risk = settings.Risk with { CooldownCyclesAfterLoss = 0 } });

        for (var trade = 0; trade < 3; trade++)
        {
            await ClosedAsync(app, user, agent, -50m, app.Clock.GetUtcNow().AddDays(-8));
        }

        await app.RunAsync(user, agent);

        Assert.Equal(["ReadyToExecute"], await app.CyclesAsync(agent.Id));
        Assert.Equal(AgentStatus.Active, await app.Agents.StatusAsync(user.Id, agent.Id));
    }

    [Fact]
    public async Task A_stop_out_the_cycles_own_reconciliation_finds_counts_in_that_cycle()
    {
        await using var app = new CycleApp(postgres);
        var book = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        var (user, agent) = await app.ActiveAgentAsync((_, settings) => settings with
        {
            ExecutionMode = ExecutionMode.Autonomous,
            Risk = settings.Risk with { ConsecutiveLossesBeforePause = 1 },
        });

        await app.RunAsync(user, agent);
        book.Trigger(book.Orders.Single(order => order.Type == "STOP_LOSS").ClientOrderId);

        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.RunAsync(user, agent);

        Assert.Equal(["PausedForLossLimit", "Executed"], await app.CyclesAsync(agent.Id));
        Assert.Equal(AgentStatus.Paused, await app.Agents.StatusAsync(user.Id, agent.Id));
        Assert.Equal(
            ["Closed StopFilled"],
            await Sql.ColumnAsync(app.ConnectionString, $"SELECT concat_ws(' ', status, exit_reason) FROM trades WHERE agent_id = '{agent.Id}'", Cancellation));
    }

    /// <summary>A trade of the agent's that closed at <paramref name="closedAt"/> with <paramref name="pnl"/>, as the journal books one.</summary>
    private static Task ClosedAsync(CycleApp app, AgentApp.User user, AgentRecord agent, decimal pnl, DateTimeOffset closedAt) =>
        Sql.ExecuteAsync(
            app.ConnectionString,
            $"""
            INSERT INTO trades (id, user_id, agent_id, entry_id, credential_id, market, environment, symbol, base_asset, quote_asset, side, status,
                opened_at, closed_at, quantity, entry_price, initial_stop, exit_quantity, exit_price, unpriced_quantity, fees, funding, realized_pnl,
                exit_reason, settled_at, settle_attempts, created_at, updated_at)
            VALUES ('{Guid.NewGuid()}', '{user.Id}', '{agent.Id}', '{Guid.NewGuid()}', '{user.ExchangeKey}', 'Spot', 'Demo', 'BTCUSDT', 'BTC', 'USDT',
                'Long', 'Closed', '{closedAt.AddHours(-1):O}', '{closedAt:O}', 0.001, 60000, 59900, 0.001, 59900, 0, 0, 0,
                {pnl.ToString(CultureInfo.InvariantCulture)}, 'StopFilled', '{closedAt:O}', 0, '{closedAt:O}', '{closedAt:O}')
            """,
            Cancellation);
}
