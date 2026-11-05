using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-RISK-08: when a daily or weekly loss limit or the consecutive-loss count is reached, the agent pauses for the
/// applicable period and its user is told. A daily or weekly pause can't be ended early, and ends on its own.
/// </summary>
public sealed class AgentLossLimitTests(PostgresFixture postgres)
{
    private const string ResumeJob = "LossLimitResumeJob";

    private static CancellationToken Cancellation => AgentApp.Cancellation;

    private static LossLimitBreach Daily(AgentApp app) => new(LossLimitKind.Daily, RiskPeriods.NextDay(app.Clock.GetUtcNow()));

    [Fact]
    public async Task A_daily_limit_pauses_the_agent_until_the_next_day_by_the_system_and_tells_its_user()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        var breach = Daily(app);

        Assert.True(await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, breach, Cancellation));

        var paused = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal((AgentStatus.Paused, AgentStatusReason.DailyLossLimit, breach.Until), (paused.Status, paused.StatusReason, paused.LossLimitUntil));

        var record = (await app.AuditAsync(user.Id)).Last(row => row.Action == nameof(AuditAction.AgentPaused));
        Assert.Equal("System", record.ActorKind);
        Assert.Contains("DailyLossLimit", record.Summary, StringComparison.Ordinal);
        Assert.Contains("PausedUntil", record.Summary, StringComparison.Ordinal);

        var notice = Assert.Single(await Inbox(app).ListAsync(user.Id, before: null, Cancellation));
        Assert.Equal(
            (NotificationType.AgentPausedLossLimit, $"/agents/{agent.Id}/settings", "Scalper", true),
            (notice.Type, notice.Link, notice.Subject, notice.Demo));
    }

    [Fact]
    public async Task Only_an_active_agent_is_paused_and_its_user_told_once()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        var draft = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Draft"));

        Assert.True(await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, Daily(app), Cancellation));
        Assert.False(await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, Daily(app), Cancellation));
        Assert.False(await app.Agents.PauseForLossLimitAsync(user.Id, draft.Id, Daily(app), Cancellation));

        // Another user's agent behaves like one that doesn't exist (NFR-SEC-04).
        var other = await app.UserAsync();
        Assert.False(await app.Agents.PauseForLossLimitAsync(other.Id, agent.Id, Daily(app), Cancellation));

        Assert.Single(await Inbox(app).ListAsync(user.Id, before: null, Cancellation));
        Assert.Equal(AgentStatus.Draft, await app.StatusAsync(user.Id, draft.Id));
    }

    [Fact]
    public async Task A_pause_whose_period_has_ended_does_nothing()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        var ended = new LossLimitBreach(LossLimitKind.Daily, app.Clock.GetUtcNow());

        Assert.False(await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, ended, Cancellation));
        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));
    }

    [Fact]
    public async Task A_breach_must_have_an_end_exactly_when_its_limit_has_a_period()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);

        await Assert.ThrowsAsync<ArgumentException>(
            () => app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, new LossLimitBreach(LossLimitKind.Weekly, null), Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(
            () => app.Agents.PauseForLossLimitAsync(
                user.Id, agent.Id, new LossLimitBreach(LossLimitKind.ConsecutiveLosses, app.Clock.GetUtcNow().AddDays(1)), Cancellation));
    }

    [Fact]
    public async Task The_user_cannot_resume_early_even_by_stopping_the_agent_first()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        var breach = Daily(app);
        await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, breach, Cancellation);

        var resumed = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Refused, resumed.Outcome);
        var problem = Assert.Single(resumed.Problems!);
        Assert.Equal((ActivationProblemKind.LossLimitPause, breach.Until), (problem.Kind, problem.Until));

        Assert.Equal(TransitionOutcome.Done, (await app.Agents.StopAsync(user.Id, agent.Id, Cancellation)).Outcome);
        Assert.Equal(ActivationOutcome.Refused, (await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)).Outcome);

        // Once the period is over, the user may activate it.
        app.Clock.Advance(breach.Until!.Value - app.Clock.GetUtcNow());
        var activated = await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, activated.Outcome);
        Assert.Null(activated.Agent!.LossLimitUntil);
        Assert.Equal(AgentStatusReason.User, activated.Agent.StatusReason);
    }

    [Fact]
    public async Task A_daily_or_weekly_pause_ends_on_its_own_by_the_system()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var daily = await app.ActiveAsync(user, "Daily");
        var weekly = await app.ActiveAsync(user, "Weekly", Market.Futures);
        var now = app.Clock.GetUtcNow();
        await app.Agents.PauseForLossLimitAsync(user.Id, daily.Id, new LossLimitBreach(LossLimitKind.Daily, RiskPeriods.NextDay(now)), Cancellation);
        await app.Agents.PauseForLossLimitAsync(user.Id, weekly.Id, new LossLimitBreach(LossLimitKind.Weekly, RiskPeriods.NextWeek(now) + TimeSpan.FromDays(7)), Cancellation);

        // Not yet.
        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync(ResumeJob));
        Assert.Equal(AgentStatus.Paused, await app.StatusAsync(user.Id, daily.Id));

        app.Clock.Advance(RiskPeriods.NextDay(now) - now);
        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync(ResumeJob));

        var resumed = (await app.Agents.FindAsync(user.Id, daily.Id, Cancellation))!;
        Assert.Equal((AgentStatus.Active, AgentStatusReason.LossLimitEnded), (resumed.Status, resumed.StatusReason));
        Assert.Null(resumed.LossLimitUntil);
        Assert.Equal(AgentStatus.Paused, await app.StatusAsync(user.Id, weekly.Id));

        var record = (await app.AuditAsync(user.Id)).Last(row => row.Action == nameof(AuditAction.AgentActivated));
        Assert.Equal(("System", daily.Id.ToString()), (record.ActorKind, record.TargetId));
        Assert.Contains("LossLimitEnded", record.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_fails_its_checks_when_the_pause_ends_stays_paused_for_its_user()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        var breach = Daily(app);
        await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, breach, Cancellation);

        // The exchange key goes while the agent is paused.
        await app.Credentials.DeleteAsync(user.Id, user.ExchangeKey, Cancellation);
        app.Clock.Advance(breach.Until!.Value - app.Clock.GetUtcNow());

        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync(ResumeJob));

        var held = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal((AgentStatus.Paused, AgentStatusReason.DailyLossLimit), (held.Status, held.StatusReason));
        Assert.Null(held.LossLimitUntil);

        // It's left to the user from then on: the next run doesn't try again.
        var version = held.Version;
        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync(ResumeJob));
        Assert.Equal(version, (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!.Version);
    }

    [Fact]
    public async Task The_job_leaves_an_agent_the_user_stopped_during_its_pause()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);
        var breach = Daily(app);
        await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, breach, Cancellation);
        await app.Agents.StopAsync(user.Id, agent.Id, Cancellation);

        app.Clock.Advance(breach.Until!.Value - app.Clock.GetUtcNow());
        await app.RunJobAsync(ResumeJob);

        Assert.Equal(AgentStatus.Stopped, await app.StatusAsync(user.Id, agent.Id));
    }

    [Fact]
    public async Task The_consecutive_loss_pause_lasts_until_the_user_resumes()
    {
        await using var app = new AgentApp(postgres);
        var user = await app.UserAsync();
        var agent = await app.ActiveAsync(user);

        Assert.True(await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, new LossLimitBreach(LossLimitKind.ConsecutiveLosses, null), Cancellation));

        var paused = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal((AgentStatusReason.ConsecutiveLosses, (DateTimeOffset?)null), (paused.StatusReason, paused.LossLimitUntil));

        // The job never resumes it, however long it waits.
        app.Clock.Advance(TimeSpan.FromDays(30));
        await app.RunJobAsync(ResumeJob);
        Assert.Equal(AgentStatus.Paused, await app.StatusAsync(user.Id, agent.Id));

        // The user may resume it at any time.
        Assert.Equal(ActivationOutcome.Activated, (await app.Agents.ActivateAsync(user.Id, agent.Id, Cancellation)).Outcome);
    }

    private static INotifications Inbox(AgentApp app) => app.App.Services.GetRequiredService<INotifications>();
}
