using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// What happens around a cycle: one that can't decide, and the Error status after too many in a row (FR-AGT-04,
/// FR-NTF-01), an agent paused before it starts, the same candle twice, deleting an agent, whose trace a user can
/// read, and what a crash leaves.
/// </summary>
public sealed class CycleLifecycleTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>During maintenance Binance can have no candle for the close, and the cycle can't decide on stale data.</summary>
    [Fact]
    public async Task A_missing_candle_fails_the_cycle_before_the_council()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        MissTheLatestCandle(app);

        await app.RunAsync(user, agent);

        Assert.Equal(["Failed MarketData"], await app.CyclesAsync(agent.Id));
        Assert.Empty(await app.CallsAsync(agent.Id));
        Assert.Equal(["TechnicalAnalyst Skipped", "Strategist Skipped", "RiskCritic Skipped"], await app.RolesAsync(user.Id, agent.Id));
    }

    [Fact]
    public async Task Three_failed_cycles_in_a_row_put_the_agent_in_error_and_tell_its_user()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        MissTheLatestCandle(app);

        await RunHourlyAsync(app, user, agent, 2);
        Assert.Equal(AgentStatus.Active, await app.Agents.StatusAsync(user.Id, agent.Id));

        await RunHourlyAsync(app, user, agent, 1);

        var record = await app.Agents.Agents.FindAsync(user.Id, agent.Id, CycleApp.Cancellation);
        Assert.Equal((AgentStatus.Error, AgentStatusReason.RepeatedCycleErrors), (record!.Status, record.StatusReason));

        var errored = Assert.Single(await app.Agents.AuditAsync(user.Id), row => row.Action == nameof(AuditAction.AgentErrored));
        Assert.Equal(nameof(AuditActorKind.System), errored.ActorKind);

        var notices = await app.Agents.App.Services.GetRequiredService<INotifications>().ListAsync(user.Id, null, CycleApp.Cancellation);
        Assert.Contains(notices, notice => notice.Type == NotificationType.AgentPausedErrors && notice.Demo);
    }

    /// <summary>A cycle that decides ends the run, so only failures in a row count.</summary>
    [Fact]
    public async Task A_cycle_that_decides_starts_the_count_again()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();

        MissTheLatestCandle(app);
        await RunHourlyAsync(app, user, agent, 2);

        app.Missing = null;
        await RunHourlyAsync(app, user, agent, 1);

        MissTheLatestCandle(app);
        await RunHourlyAsync(app, user, agent, 2);

        Assert.Equal(AgentStatus.Active, await app.Agents.StatusAsync(user.Id, agent.Id));
    }

    [Fact]
    public async Task A_limit_of_zero_never_puts_the_agent_in_error()
    {
        await using var app = new CycleApp(postgres, new() { ["Agents:CycleErrorsBeforePause"] = "0" });
        var (user, agent) = await app.ActiveAgentAsync();
        MissTheLatestCandle(app);

        await RunHourlyAsync(app, user, agent, 4);

        Assert.Equal(AgentStatus.Active, await app.Agents.StatusAsync(user.Id, agent.Id));
        Assert.Equal(4, (await app.CyclesAsync(agent.Id)).Count);
    }

    /// <summary>FR-AGT-06: the scheduler listed it a moment ago, but a pause since stops the cycle before it starts.</summary>
    [Fact]
    public async Task An_agent_paused_before_its_cycle_starts_runs_none()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        await app.Agents.Agents.PauseAsync(user.Id, agent.Id, CycleApp.Cancellation);

        await app.RunAsync(user, agent);

        Assert.Empty(await app.CyclesAsync(agent.Id));
        Assert.Null(await app.Traces.FindLatestAsync(user.Id, agent.Id, CycleApp.Cancellation));
    }

    [Fact]
    public async Task A_candle_gets_one_cycle_however_often_it_runs()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();

        await app.RunAsync(user, agent);
        await app.RunAsync(user, agent);

        Assert.Equal(["AwaitingApproval"], await app.CyclesAsync(agent.Id));
        Assert.Equal(3, (await app.CallsAsync(agent.Id)).Count);
    }

    /// <summary>Deleting an agent for good takes its trace with it.</summary>
    [Fact]
    public async Task Deleting_an_agent_deletes_its_cycles()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);

        await app.Agents.Agents.PauseAsync(user.Id, agent.Id, CycleApp.Cancellation);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.Agents.ArchiveAsync(user.Id, agent.Id, CycleApp.Cancellation)).Outcome);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.Agents.DeleteAsync(user.Id, agent.Id, CycleApp.Cancellation)).Outcome);

        Assert.Empty(await app.CyclesAsync(agent.Id));
        Assert.Equal(0L, await Sql.ScalarAsync<long>(app.ConnectionString, "SELECT count(*) FROM ai_calls", CycleApp.Cancellation));
        Assert.Equal(0L, await Sql.ScalarAsync<long>(app.ConnectionString, "SELECT count(*) FROM risk_evaluations", CycleApp.Cancellation));
    }

    /// <summary>NFR-SEC-04: another user's agent behaves like one with no cycles.</summary>
    [Fact]
    public async Task Only_its_user_reads_an_agents_trace()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);

        Assert.NotNull(await app.Traces.FindLatestAsync(user.Id, agent.Id, CycleApp.Cancellation));
        Assert.Null(await app.Traces.FindLatestAsync(Guid.NewGuid(), agent.Id, CycleApp.Cancellation));
    }

    /// <summary>A cycle a crash left running is interrupted when the application starts again, with its unfinished roles skipped.</summary>
    [Fact]
    public async Task A_cycle_a_crash_left_running_is_interrupted_at_the_next_start()
    {
        await using var app = new CycleApp(postgres);
        var (user, agent) = await app.ActiveAgentAsync();
        await app.RunAsync(user, agent);
        await Sql.ExecuteAsync(
            app.ConnectionString,
            $"""
            UPDATE decision_cycles SET outcome = NULL, finished_at = NULL WHERE agent_id = '{agent.Id}';
            UPDATE cycle_roles SET state = 'Running' WHERE role = 'RiskCritic';
            """,
            CycleApp.Cancellation);

        await using (var restarted = new AppFactory(app.ConnectionString))
        {
            restarted.Start();
        }

        Assert.Equal(["Interrupted"], await app.CyclesAsync(agent.Id));
        Assert.Contains("RiskCritic Skipped", await app.RolesAsync(user.Id, agent.Id));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("three")]
    public async Task An_invalid_error_limit_stops_startup_and_is_named(string value)
    {
        const string Key = "Agents:CycleErrorsBeforePause";
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { [Key] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(Key, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Binance leaves out the candle that closed last, whenever a cycle asks.</summary>
    private static void MissTheLatestCandle(CycleApp app) => app.Missing = open => open >= app.LastClose - Hour;

    /// <summary>Runs <paramref name="count"/> cycles, an hour apart, each for the candle that closed then.</summary>
    private static async Task RunHourlyAsync(CycleApp app, Agents.AgentApp.User user, AgentRecord agent, int count)
    {
        for (var run = 0; run < count; run++)
        {
            app.Clock.Advance(Hour);
            await app.RunAsync(user, agent);
        }
    }
}
