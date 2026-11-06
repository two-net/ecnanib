using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Identity.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-AGT-07, FR-AGT-08, FR-AUTH-05, UC-05: Agents' side of the kill switch and "Stop all agents". The agent stops with
/// <see cref="AgentStatusReason.KillSwitch"/> before execution is asked to close it, with an audit record and a notice,
/// and only once a code has passed when two-factor is on. <see cref="FakeLiquidation"/> stands in for execution, whose
/// side <c>KillSwitchTests</c> covers.
/// </summary>
public sealed class AgentKillSwitchTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task The_kill_switch_stops_the_agent_before_execution_closes_it_and_audits_and_notifies_it()
    {
        var liquidation = new FakeLiquidation();
        await using var app = App(liquidation);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        AgentRecord? seen = null;
        liquidation.Close = async (userId, agentId) =>
        {
            seen = await app.Agents.FindAsync(userId, agentId, Cancellation);
            return LiquidationOutcome.Closed;
        };

        var result = await app.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);

        Assert.Equal(KillOutcome.Closed, result.Outcome);
        Assert.Equal((AgentStatus.Stopped, AgentStatusReason.KillSwitch), (result.Agent!.Status, result.Agent.StatusReason));
        Assert.Equal((AgentStatus.Stopped, AgentStatusReason.KillSwitch), (seen!.Status, seen.StatusReason));
        Assert.Equal([(user.Id, agent.Id)], liquidation.Calls);

        var record = (await app.AuditAsync(user.Id)).Last();
        Assert.Equal(("AgentKillSwitch", "User", agent.Id.ToString()), (record.Action, record.ActorKind, record.TargetId));
        Assert.Contains("\"Active\"", record.Summary, StringComparison.Ordinal);
        Assert.Contains("\"KillSwitch\"", record.Summary, StringComparison.Ordinal);

        Assert.Equal([$"KillSwitchExecuted /agents/{agent.Id}/settings Scalper true"], await NoticesAsync(app, user.Id));
    }

    [Theory]
    [InlineData(AgentStatus.Paused)]
    [InlineData(AgentStatus.Error)]
    [InlineData(AgentStatus.Stopped)]
    public async Task An_agent_that_may_hold_something_can_be_killed_whatever_stopped_it(AgentStatus status)
    {
        var liquidation = new FakeLiquidation { Close = (_, _) => Task.FromResult(LiquidationOutcome.Closing) };
        await using var app = App(liquidation);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        await SetStatusAsync(app, agent.Id, status);

        var result = await app.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);

        Assert.Equal(KillOutcome.Closing, result.Outcome);
        Assert.Equal((AgentStatus.Stopped, AgentStatusReason.KillSwitch), (result.Agent!.Status, result.Agent.StatusReason));
        Assert.Single(liquidation.Calls);
        Assert.Contains($"\"{status}\"", (await app.AuditAsync(user.Id)).Last(row => row.Action == "AgentKillSwitch").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_draft_or_archived_agent_has_nothing_to_kill()
    {
        var liquidation = new FakeLiquidation();
        await using var app = App(liquidation);
        var user = await app.UserAsync();
        var draft = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Draft"));
        var archived = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Archived"));
        await app.Agents.ArchiveAsync(user.Id, archived.Id, Cancellation);
        var records = (await app.AuditAsync(user.Id)).Count;

        Assert.Equal(KillOutcome.NotAllowed, (await app.Agents.StopAndCloseAsync(user.Id, draft.Id, null, Cancellation)).Outcome);
        Assert.Equal(KillOutcome.NotAllowed, (await app.Agents.StopAndCloseAsync(user.Id, archived.Id, null, Cancellation)).Outcome);
        Assert.Equal(KillOutcome.NotFound, (await app.Agents.StopAndCloseAsync(user.Id, Guid.NewGuid(), null, Cancellation)).Outcome);

        Assert.Empty(liquidation.Calls);
        Assert.Equal(records, (await app.AuditAsync(user.Id)).Count);
        Assert.Equal(AgentStatus.Draft, await app.StatusAsync(user.Id, draft.Id));
    }

    /// <summary>The agent is stopped either way: whatever execution couldn't do, reconciliation finishes.</summary>
    [Fact]
    public async Task An_agent_execution_couldnt_close_yet_is_stopped_and_says_so()
    {
        var liquidation = new FakeLiquidation { Close = (_, _) => throw new InvalidOperationException("The database went away.") };
        await using var app = App(liquidation);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);

        var result = await app.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);

        Assert.Equal(KillOutcome.Unconfirmed, result.Outcome);
        Assert.Equal((AgentStatus.Stopped, AgentStatusReason.KillSwitch), (result.Agent!.Status, result.Agent.StatusReason));
    }

    /// <summary>FR-AUTH-05: the code comes before anything changes, and stopping while keeping positions needs none.</summary>
    [Fact]
    public async Task With_two_factor_on_the_kill_switch_needs_a_code_and_changes_nothing_without_one()
    {
        var liquidation = new FakeLiquidation();
        await using var app = App(liquidation);
        var (user, account, visitor) = await TwoFactorUserAsync(app);
        using var _ = visitor;
        var agent = await app.ActiveAsync(user);
        var kept = await app.ActiveAsync(user, "Kept", Market.Futures);
        var records = (await app.AuditAsync(user.Id)).Count;

        var missing = await app.Agents.StopAndCloseAsync(user.Id, agent.Id, null, Cancellation);
        var wrong = await app.Agents.StopAndCloseAsync(user.Id, agent.Id, "000000", Cancellation);

        Assert.Equal((KillOutcome.TwoFactorRefused, (TwoFactorCheck?)TwoFactorCheck.Refused), (missing.Outcome, missing.TwoFactor));
        Assert.Equal((KillOutcome.TwoFactorRefused, (TwoFactorCheck?)TwoFactorCheck.Refused), (wrong.Outcome, wrong.TwoFactor));
        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));
        Assert.Empty(liquidation.Calls);
        Assert.Equal(records, (await app.AuditAsync(user.Id)).Count);
        Assert.Empty(await NoticesAsync(app, user.Id));

        var right = await app.Agents.StopAndCloseAsync(user.Id, agent.Id, app.Clock.NextCode(account.Key), Cancellation);

        Assert.Equal(KillOutcome.Closed, right.Outcome);
        Assert.Single(liquidation.Calls);

        // Keeping positions isn't the kill switch, so it takes no code (FR-AGT-07).
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.StopAsync(user.Id, kept.Id, Cancellation)).Outcome);
        Assert.Single(liquidation.Calls);
    }

    /// <summary>An owner who mistypes codes in a hurry still reaches the kill switch with a recovery code.</summary>
    [Fact]
    public async Task After_too_many_wrong_codes_a_recovery_code_still_reaches_the_kill_switch()
    {
        var liquidation = new FakeLiquidation();
        await using var app = App(liquidation);
        var (user, account, visitor) = await TwoFactorUserAsync(app);
        using var _ = visitor;
        var agent = await app.ActiveAsync(user);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await app.Agents.StopAndCloseAsync(user.Id, agent.Id, "000000", Cancellation);
        }

        var throttled = await app.Agents.StopAndCloseAsync(user.Id, agent.Id, app.Clock.NextCode(account.Key), Cancellation);
        var recovered = await app.Agents.StopAndCloseAsync(user.Id, agent.Id, account.RecoveryCodes[0], Cancellation);

        Assert.Equal((KillOutcome.TwoFactorRefused, (TwoFactorCheck?)TwoFactorCheck.Throttled), (throttled.Outcome, throttled.TwoFactor));
        Assert.Equal(KillOutcome.Closed, recovered.Outcome);
    }

    /// <summary>
    /// UC-05: one code for them all, then the kill switch for every agent that runs, is paused or in error, or is stopped
    /// and still holds something, oldest first, each with a result of its own.
    /// </summary>
    [Fact]
    public async Task Stop_all_checks_the_code_once_and_kills_every_agent_that_runs_or_holds_something()
    {
        var liquidation = new FakeLiquidation();
        var exposure = new FakeExposure();
        await using var app = App(liquidation, exposure);
        var second = Guid.Empty;
        var (user, account, visitor) = await TwoFactorUserAsync(app, async userId => second = await app.AddBinanceAsync(userId, "Second account"));
        using var _ = visitor;

        // One per key, market and pair may hold the slot (FR-AGT-10), so each runs somewhere of its own.
        var active = await app.ActiveAsync(user, "Active");
        var paused = await app.ActiveAsync(user, "Paused", Market.Futures);
        var errored = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Errored") with { ExchangeCredentialId = second });
        var holding = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Holding"));
        var stopped = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Stopped"));
        var draft = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Draft"));
        await app.Agents.PauseAsync(user.Id, paused.Id, Cancellation);
        await SetStatusAsync(app, errored.Id, AgentStatus.Error);
        await SetStatusAsync(app, holding.Id, AgentStatus.Stopped);
        await SetStatusAsync(app, stopped.Id, AgentStatus.Stopped);
        exposure.Exposed.Add(holding.Id);

        var refused = await app.Agents.StopAllAsync(user.Id, "000000", Cancellation);

        Assert.True(refused.Refused);
        Assert.Empty(refused.Agents);
        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, active.Id));
        Assert.Empty(liquidation.Calls);

        var result = await app.Agents.StopAllAsync(user.Id, app.Clock.NextCode(account.Key), Cancellation);

        Assert.False(result.Refused);
        Assert.Equal(["Active Closed", "Paused Closed", "Errored Closed", "Holding Closed"], result.Agents.Select(agent => $"{agent.Name} {agent.Result.Outcome}"));
        Assert.Equal([active.Id, paused.Id, errored.Id, holding.Id], liquidation.Calls.Select(call => call.AgentId));

        foreach (var killed in new[] { active, paused, errored, holding })
        {
            var now = (await app.Agents.FindAsync(user.Id, killed.Id, Cancellation))!;
            Assert.Equal((AgentStatus.Stopped, AgentStatusReason.KillSwitch), (now.Status, now.StatusReason));
        }

        Assert.Equal(AgentStatusReason.User, (await app.Agents.FindAsync(user.Id, stopped.Id, Cancellation))!.StatusReason);
        Assert.Equal(AgentStatus.Draft, await app.StatusAsync(user.Id, draft.Id));
        Assert.Equal(4, (await app.AuditAsync(user.Id)).Count(row => row.Action == "AgentKillSwitch"));
        Assert.Equal(4, (await NoticesAsync(app, user.Id)).Count(notice => notice.StartsWith("KillSwitchExecuted", StringComparison.Ordinal)));
    }

    /// <summary>NFR-REL-05: one agent that fails doesn't keep the rest running.</summary>
    [Fact]
    public async Task One_agent_failing_doesnt_keep_stop_all_from_the_rest()
    {
        var liquidation = new FakeLiquidation();
        await using var app = App(liquidation);
        var user = await app.UserAsync();
        var first = await app.ActiveAsync(user, "First");
        var next = await app.ActiveAsync(user, "Next", Market.Futures);
        liquidation.Close = (_, agentId) => agentId == first.Id
            ? throw new InvalidOperationException("Binance went away.")
            : Task.FromResult(LiquidationOutcome.NothingOpen);

        var result = await app.Agents.StopAllAsync(user.Id, null, Cancellation);

        Assert.Equal(["First Unconfirmed", "Next NothingOpen"], result.Agents.Select(agent => $"{agent.Name} {agent.Result.Outcome}"));
        Assert.Equal(AgentStatus.Stopped, await app.StatusAsync(user.Id, first.Id));
        Assert.Equal(AgentStatus.Stopped, await app.StatusAsync(user.Id, next.Id));
    }

    [Fact]
    public async Task Stop_all_with_nothing_running_does_nothing()
    {
        var liquidation = new FakeLiquidation();
        await using var app = App(liquidation);
        var user = await app.UserAsync();
        await app.CreateAsync(user.Id, AgentApp.Ready(user));

        var result = await app.Agents.StopAllAsync(user.Id, null, Cancellation);

        Assert.False(result.Refused);
        Assert.Empty(result.Agents);
        Assert.Empty(liquidation.Calls);
    }

    private AgentApp App(FakeLiquidation liquidation, FakeExposure? exposure = null) =>
        new(postgres, exposure: exposure, services: services => services.Replace(ServiceDescriptor.Singleton<IAgentLiquidation>(liquidation)));

    /// <summary>
    /// An account with its keys, then two-factor turned on, because adding an exchange key would need a code after.
    /// The visitor holds the session that turned it on.
    /// </summary>
    /// <param name="before">Anything else to do before two-factor is on, such as adding another exchange key.</param>
    private static async Task<(AgentApp.User User, TwoFactorAccount Account, Visitor Visitor)> TwoFactorUserAsync(
        AgentApp app, Func<Guid, Task>? before = null)
    {
        var visitor = Visitor.To(app.App);
        var email = await visitor.SignUpAsync();
        var userId = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var user = new AgentApp.User(userId, await app.AddBinanceAsync(userId), await app.AddAiAsync(userId));

        if (before is not null)
        {
            await before(userId);
        }

        var key = await TwoFactorSteps.StartAsync(visitor);
        var codes = await TwoFactorSteps.EnableAsync(visitor, key, app.Clock);

        return (user, new TwoFactorAccount(email, key, codes), visitor);
    }

    private static Task SetStatusAsync(AgentApp app, Guid agentId, AgentStatus status) =>
        Sql.ExecuteAsync(app.ConnectionString, $"UPDATE agents SET status = '{status}', version = version + 1 WHERE id = '{agentId}'", Cancellation);

    /// <summary>The user's notifications, oldest first, as type, link, subject and whether it's Demo.</summary>
    private static Task<List<string>> NoticesAsync(AgentApp app, Guid userId) =>
        Sql.ColumnAsync(
            app.ConnectionString,
            $"SELECT concat_ws(' ', type, link, payload ->> 'subject', payload ->> 'demo') FROM notifications WHERE user_id = '{userId}' ORDER BY id",
            Cancellation);
}

/// <summary>Stands in for execution's side of the kill switch, and records who it was asked to close.</summary>
internal sealed class FakeLiquidation : IAgentLiquidation
{
    private readonly ConcurrentQueue<(Guid UserId, Guid AgentId)> _calls = new();

    public IReadOnlyList<(Guid UserId, Guid AgentId)> Calls => [.. _calls];

    public Func<Guid, Guid, Task<LiquidationOutcome>> Close { get; set; } = (_, _) => Task.FromResult(LiquidationOutcome.Closed);

    public async Task<LiquidationOutcome> CloseAllAsync(Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        _calls.Enqueue((userId, agentId));
        return await Close(userId, agentId);
    }
}
