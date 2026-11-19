using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// When an agent's loss for the UTC day reaches the daily loss limit, the system closes its
/// position and halts it until the next UTC day. When its drawdown from peak equity reaches the
/// drawdown limit, the system closes its position and halts it until the user resumes it
/// (FR-RSK-8). The agent enters the Halted state, with the cause and time recorded and shown to
/// the user (FR-AGT-10). This is what Agents does of it: it halts an agent, says why and until
/// when, refuses a resume before the halt of a day is over, and resumes the agent after it. See
/// "Halting an agent" in docs/agents.md.
/// </summary>
/// <remarks>
/// What works out that a limit is reached, and closes the position, is shown in
/// <c>LossLimitHaltTests</c> of <c>tests/Ecnanib.Tests/DecisionEngine</c>.
/// </remarks>
public class AgentHaltTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string HaltedTemplate = "Agent {AgentId} of user {UserId} is halted: {AgentStateCause}";

    private const string RunsAgainTemplate =
        "Agent {AgentId} of user {UserId} runs again: the UTC day in which its daily loss limit halted it is over";

    private const string ResumeButton = "<button type=\"submit\">Resume the agent</button>";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDay = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentStateCause.DailyLossLimitReached)]
    [InlineData(AgentStateCause.DrawdownLimitReached)]
    public async Task An_agent_that_runs_is_halted_for_a_loss_limit_with_the_cause_and_the_time(AgentStateCause cause)
    {
        var clock = new TestClock(Morning.AddHours(2));
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, state: AgentState.Running, cause: AgentStateCause.UserStarted, mode: TradingMode.Live);
        var at = Morning.AddHours(1);

        // Inside the transaction of what watches the loss limits, which commits.
        Assert.True(await HaltAsync(application, user.Id, agent, cause, at));

        // The row of the agent and the change of its state say the same: the state, why, and
        // since when, which is the moment that the limit was found reached at, and the mode
        // that the agent was in (FR-AGT-10, FR-BIN-11).
        Assert.Equal(("Halted", cause.ToString(), at), AgentControlTests.StateOf(application, agent));

        var change = StoredAgentStateChange.Of(application.Database, agent)[^1];
        Assert.Equal(("Live", user.Id), (change.Mode, change.UserId));

        // It is the service's doing, and no action of a user: the audit trail has nothing of it.
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));

        var said = Assert.Single(application.Events(), written => written.Template == HaltedTemplate);
        Assert.Equal(("Warning", agent.ToString(), cause.ToString()), (said.Level, said.Text("AgentId"), said.Text("AgentStateCause")));

        // A halted agent is not halted a second time.
        Assert.False(await HaltAsync(application, user.Id, agent, cause, at.AddMinutes(5)));
        Assert.Equal(at, AgentControlTests.StateOf(application, agent).ChangedAt);
    }

    [Theory]
    [InlineData(AgentState.Draft)]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.AttentionRequired)]
    [InlineData(AgentState.Stopped)]
    public async Task An_agent_that_does_not_run_is_not_halted(AgentState state)
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning.AddHours(1)));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, state: state);
        var stored = application.RowsOf(user.Id);

        // One that its user paused or stopped, or that waits for its user, stays as it is: its
        // position stays open with its protective orders (SRS 3.3).
        Assert.False(await HaltAsync(application, user.Id, agent, AgentStateCause.DailyLossLimitReached, Morning.AddHours(1)));

        Assert.Equal(stored, application.RowsOf(user.Id));
    }

    [Fact]
    public async Task An_agent_is_halted_for_a_loss_limit_and_for_nothing_else_inside_a_transaction_and_for_its_own_user()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning.AddHours(1)));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var agent = await application.GiveAgentAsync(ada.Id, Morning, state: AgentState.Running);
        var stored = application.RowsOf(ada.Id);

        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        // With no transaction, the halt could stand when what its limit is counted from does not.
        await Assert.ThrowsAsync<InvalidOperationException>(() => application.AgentHalts.HaltAsync(
            context, ada.Id, agent, AgentStateCause.DailyLossLimitReached, Morning, TestContext.Current.CancellationToken));

        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            // No other cause is one that halts.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => application.AgentHalts.HaltAsync(
                context, ada.Id, agent, AgentStateCause.PositionUnprotected, Morning, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => application.AgentHalts.HaltAsync(
                context, ada.Id, agent, AgentStateCause.DailyLossHaltEnded, Morning, TestContext.Current.CancellationToken));

            // The agent of one user is not halted as that of another.
            Assert.False(await application.AgentHalts.HaltAsync(
                context, grace.Id, agent, AgentStateCause.DailyLossLimitReached, Morning, TestContext.Current.CancellationToken));

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(stored, application.RowsOf(ada.Id));

        // A halt that is not committed is no halt: what watches writes both, or neither.
        await using (await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await application.AgentHalts.HaltAsync(
                context, ada.Id, agent, AgentStateCause.DrawdownLimitReached, Morning, TestContext.Current.CancellationToken));
        }

        Assert.Equal(stored, application.RowsOf(ada.Id));
    }

    [Fact]
    public async Task The_page_of_a_halted_agent_says_the_cause_since_when_and_when_the_halt_of_a_day_ends()
    {
        var clock = new TestClock(Morning.AddHours(1));
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        var daily = await application.GiveAgentAsync(
            user.Id, Morning, name: "Daily", state: AgentState.Halted, cause: AgentStateCause.DailyLossLimitReached);
        var drawdown = await application.GiveAgentAsync(
            user.Id, Morning, name: "Drawdown", state: AgentState.Halted, symbol: "SOLUSDT", cause: AgentStateCause.DrawdownLimitReached);

        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(daily)));

        // The state, since when, what it means, and why (FR-AGT-10), and for a daily loss
        // limit until when: the next UTC day.
        Assert.Contains(
            $"<p>State: <strong>Halted</strong>, since 12 November 2026, 09:00 UTC. {AgentPages.Explain(AgentState.Halted)}</p>", page);
        Assert.Contains($"<p>{AgentPages.Says(AgentStateCause.DailyLossLimitReached)}</p>", page);
        Assert.Contains(
            "<p>The agent is halted until 13 November 2026, 00:00 UTC, and cannot be resumed before. From then on the service resumes " +
            "it with the first candle that closes, when your Binance key and its team allow it, and you can resume it yourself.</p>",
            page);

        // The change is in the history of the agent, with its time and its cause in words.
        Assert.Contains($"<td>12 November 2026, 09:00 UTC</td>", page);
        Assert.Contains($"<td>{AgentPages.Says(AgentStateCause.DailyLossLimitReached)}</td>", page);

        // An agent that its drawdown limit halted says that, and no time: its user resumes it.
        page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(drawdown)));

        Assert.Contains($"<p>{AgentPages.Says(AgentStateCause.DrawdownLimitReached)}</p>", page);
        Assert.DoesNotContain("The agent is halted until", page);
        Assert.Contains(ResumeButton, page);
    }

    [Fact]
    public async Task An_agent_that_its_daily_loss_limit_halted_is_not_resumed_by_its_user_before_the_next_UTC_day()
    {
        var clock = new TestClock(Morning.AddHours(1));
        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), clock, exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(
            user.Id, Morning, state: AgentState.Halted, market: Market.Futures, cause: AgentStateCause.DailyLossLimitReached);
        var stored = application.RowsOf(user.Id);
        var refusal =
            "<p class=\"form-error\" role=\"alert\">The agent was not resumed: it reached its daily loss limit, and is halted until " +
            "13 November 2026, 00:00 UTC. The service resumes it then.</p>";

        // On the day of the halt, and up to its last second: the resume changes nothing, and
        // the page says why and until when. Neither AI output nor a user action bypasses the
        // Risk Guard (FR-RSK-3).
        foreach (var moment in new[] { Morning.AddHours(1), NextDay.AddSeconds(-1) })
        {
            // Its user signs in again: a session does not last the day.
            clock.Set(moment);
            await client.SignInAsync(Ada);

            var (path, answer) = await client.ResumeAgentAsync(agent);

            Assert.Equal(AgentBrowser.AgentStatePage(agent), path);
            Assert.Contains(refusal, AgentBrowser.Read(answer));
        }

        Assert.Equal(stored["agents"], application.RowsOf(user.Id)["agents"]);
        Assert.Equal(stored["agent_state_changes"], application.RowsOf(user.Id)["agent_state_changes"]);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));

        // The exchange is asked nothing about the account for a resume that is refused anyway.
        Assert.Empty(exposure.AccountsAsked);

        Assert.Contains(
            application.Events(),
            written => written.Template ==
                "User {UserId} was refused the control {AgentControl} of agent {AgentId}: its daily loss limit halted it until the next UTC day");

        // The next UTC day has begun: its user resumes it, as the service does.
        clock.Set(NextDay);

        Assert.Equal(AgentBrowser.AgentStatePage(agent), (await client.ResumeAgentAsync(agent)).Path);
        Assert.Equal(("Running", "UserResumed", NextDay), AgentControlTests.StateOf(application, agent));
        Assert.Single(exposure.AccountsAsked);
    }

    [Fact]
    public async Task Stopping_a_halted_agent_and_starting_it_again_is_no_way_around_the_halt_of_the_day()
    {
        var clock = new TestClock(Morning.AddHours(1));
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(
            user.Id, Morning, state: AgentState.Halted, cause: AgentStateCause.DailyLossLimitReached);

        // Its user stops it, which a halted agent is at any time, and changes its limits.
        Assert.Equal(AgentBrowser.AgentStatePage(agent), (await client.StopAgentAsync(agent)).Path);
        Assert.Equal(("Stopped", "UserStopped", Morning.AddHours(1)), AgentControlTests.StateOf(application, agent));

        // Starting it again on the day of the halt changes nothing: the agent does not run
        // before the next UTC day, whatever state it is in by then (FR-RSK-8, FR-RSK-3).
        clock.Set(Morning.AddHours(1).AddMinutes(10));
        var (path, answer) = await client.StartAgentAsync(agent);

        Assert.Equal(AgentBrowser.AgentStatePage(agent), path);
        Assert.Contains(
            "<p class=\"form-error\" role=\"alert\">The agent was not started: it reached its daily loss limit in this UTC day, and does " +
            "not run before 13 November 2026, 00:00 UTC. Start it from then on.</p>",
            AgentBrowser.Read(answer));
        Assert.Equal(("Stopped", "UserStopped", Morning.AddHours(1)), AgentControlTests.StateOf(application, agent));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentStarted");

        // From the next UTC day on it is started as any stopped agent is.
        clock.Set(NextDay);
        await client.SignInAsync(Ada);

        Assert.Equal(AgentBrowser.AgentStatePage(agent), (await client.StartAgentAsync(agent)).Path);
        Assert.Equal(("Running", "UserStarted", NextDay), AgentControlTests.StateOf(application, agent));
    }

    [Fact]
    public async Task The_page_that_asks_once_more_in_Live_refuses_the_resume_of_a_day_halt_as_well()
    {
        var clock = new TestClock(Morning.AddHours(1));
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(
            user.Id, Morning, state: AgentState.Halted, mode: TradingMode.Live, cause: AgentStateCause.DailyLossLimitReached);

        // Said before anything else that a resume asks: nothing that its user does now would resume it.
        var (_, answer) = await client.ConfirmControlAsync(agent, "resume");

        Assert.Contains("The agent was not resumed: it reached its daily loss limit, and is halted until 13 November 2026, 00:00 UTC.", AgentBrowser.Read(answer));
        Assert.Equal(("Halted", "DailyLossLimitReached", Morning), AgentControlTests.StateOf(application, agent));
    }

    [Fact]
    public async Task A_halted_agent_is_stopped_at_any_time_and_one_that_its_drawdown_limit_halted_is_resumed_at_any_time()
    {
        var clock = new TestClock(Morning.AddHours(1));
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        var daily = await application.GiveAgentAsync(
            user.Id, Morning, name: "Daily", state: AgentState.Halted, cause: AgentStateCause.DailyLossLimitReached);
        var drawdown = await application.GiveAgentAsync(
            user.Id, Morning, name: "Drawdown", state: AgentState.Halted, symbol: "SOLUSDT", cause: AgentStateCause.DrawdownLimitReached);

        // On the day of the halt itself.
        Assert.Equal(AgentBrowser.AgentStatePage(daily), (await client.StopAgentAsync(daily)).Path);
        Assert.Equal(("Stopped", "UserStopped", Morning.AddHours(1)), AgentControlTests.StateOf(application, daily));

        // Halted until its user resumes it: its user does.
        Assert.Equal(AgentBrowser.AgentStatePage(drawdown), (await client.ResumeAgentAsync(drawdown)).Path);
        Assert.Equal(("Running", "UserResumed", Morning.AddHours(1)), AgentControlTests.StateOf(application, drawdown));
    }

    [Fact]
    public async Task The_service_resumes_the_agents_that_their_daily_loss_limit_halted_on_an_earlier_UTC_day_when_their_key_and_their_team_allow_it()
    {
        var clock = new TestClock(NextDay.AddSeconds(2));
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveKeysAsync(ada.Id, Morning);

        // Halted yesterday, for the daily loss limit, in Live, with a Live key that Binance
        // takes: the day of the halt is over.
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();
        var over = await application.GiveAgentAsync(
            ada.Id, Morning, name: "Over", state: AgentState.Halted, mode: TradingMode.Live, cause: AgentStateCause.DailyLossLimitReached);

        // Halted at the last moment of yesterday, and at the first moment of today.
        var late = await application.GiveAgentAsync(
            ada.Id, NextDay.AddTicks(-1), name: "Late", state: AgentState.Halted, symbol: "SOLUSDT", cause: AgentStateCause.DailyLossLimitReached);
        var today = await application.GiveAgentAsync(
            ada.Id, NextDay, name: "Today", state: AgentState.Halted, symbol: "ETHUSDT", cause: AgentStateCause.DailyLossLimitReached);

        // Halted yesterday for the drawdown limit: until its user resumes it.
        var drawdown = await application.GiveAgentAsync(
            ada.Id, Morning, name: "Drawdown", state: AgentState.Halted, symbol: "BNBUSDT", cause: AgentStateCause.DrawdownLimitReached);

        // Halted yesterday with a team that cannot work: a role has no model.
        var noTeam = await application.GiveAgentAsync(
            ada.Id, Morning, name: "No team", state: AgentState.Halted, market: Market.Futures, cause: AgentStateCause.DailyLossLimitReached, team: false);

        // Halted yesterday, of a user who has no Binance key any more.
        var noKey = await application.GiveAgentAsync(
            grace.Id, Morning, name: "No key", state: AgentState.Halted, cause: AgentStateCause.DailyLossLimitReached);

        var resumed = await application.AgentHalts.EndDailyHaltsAsync(TestContext.Current.CancellationToken);

        // Two run again, with the cause and the time, in the mode that each is in. Nobody was
        // asked once more for the one in Live: its halt ends as the requirement says it does.
        Assert.Equal(2, resumed);
        Assert.Equal(("Running", "DailyLossHaltEnded", NextDay.AddSeconds(2)), AgentControlTests.StateOf(application, over));
        Assert.Equal(("Running", "DailyLossHaltEnded", NextDay.AddSeconds(2)), AgentControlTests.StateOf(application, late));
        Assert.Equal("Live", StoredAgentStateChange.Of(application.Database, over)[^1].Mode);

        // The others are as they were.
        Assert.Equal(("Halted", "DailyLossLimitReached", NextDay), AgentControlTests.StateOf(application, today));
        Assert.Equal(("Halted", "DrawdownLimitReached", Morning), AgentControlTests.StateOf(application, drawdown));
        Assert.Equal(("Halted", "DailyLossLimitReached", Morning), AgentControlTests.StateOf(application, noTeam));
        Assert.Equal(("Halted", "DailyLossLimitReached", Morning), AgentControlTests.StateOf(application, noKey));

        // It is the service's doing: the changes of the states record it, and the audit trail does not.
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));
        Assert.Equal(
            new[] { over.ToString(), late.ToString() }.Order(),
            application.Events().Where(written => written.Template == RunsAgainTemplate).Select(written => written.Text("AgentId")).Order());

        // What keeps the two halted is said, and asked about again with the next close: once
        // the user of the one has a key, it runs again without a press of theirs.
        Assert.Contains(application.Events(), written => written.Template.Contains("the Binance key for {TradingMode} blocks it", StringComparison.Ordinal));
        Assert.Contains(application.Events(), written => written.Template.Contains("of the roles of its team cannot work", StringComparison.Ordinal));

        await application.GiveExchangeKeyAsync(grace.Id, NextDay);
        clock.Set(NextDay.AddMinutes(15));

        Assert.Equal(1, await application.AgentHalts.EndDailyHaltsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("Running", "DailyLossHaltEnded", NextDay.AddMinutes(15)), AgentControlTests.StateOf(application, noKey));

        // And a run that finds nothing to end changes nothing.
        Assert.Equal(0, await application.AgentHalts.EndDailyHaltsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task What_watches_the_loss_limits_reads_an_agent_in_whatever_state_it_is()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning.AddHours(1)));
        var user = await application.RegisterVerifiedAsync(Ada);

        foreach (var state in Enum.GetValues<AgentState>())
        {
            var id = await application.GiveAgentAsync(user.Id, Morning, name: state.ToString(), state: state, symbol: $"{state}USDT");
            var agent = await application.AgentHalts.FindAsync(id, TestContext.Current.CancellationToken);

            // The agent as it is stored: whose it is, its state, and the limits that it is held to.
            Assert.NotNull(agent);
            Assert.Equal((id, user.Id, state, 5m, 20m), (agent.Id, agent.UserId, agent.State, agent.DailyLossPercent, agent.DrawdownPercent));
        }

        Assert.Null(await application.AgentHalts.FindAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(AgentState.Halted, AgentStateCause.DailyLossLimitReached, true)]
    [InlineData(AgentState.Halted, AgentStateCause.DrawdownLimitReached, false)]
    [InlineData(AgentState.Halted, AgentStateCause.Created, false)]
    [InlineData(AgentState.Running, AgentStateCause.DailyLossHaltEnded, false)]
    [InlineData(AgentState.Stopped, AgentStateCause.DailyLossLimitReached, false)]
    public void The_halt_of_a_daily_loss_limit_ends_when_the_next_UTC_day_begins_and_no_other_ends_by_itself(
        AgentState state, AgentStateCause cause, bool ends)
    {
        // Halted in the morning, in the last tick of a day, and at midnight itself, which is
        // the first moment of the day that it begins.
        Assert.Equal(ends ? NextDay : null, AgentStates.HaltEnds(state, cause, Morning));
        Assert.Equal(ends ? NextDay : null, AgentStates.HaltEnds(state, cause, NextDay.AddTicks(-1)));
        Assert.Equal(ends ? NextDay.AddDays(1) : null, AgentStates.HaltEnds(state, cause, NextDay));

        // Whatever offset a time is written with: the day is the UTC day.
        Assert.Equal(
            ends ? NextDay : null,
            AgentStates.HaltEnds(state, cause, new DateTimeOffset(2026, 11, 13, 6, 30, 0, TimeSpan.FromHours(7))));
    }

    /// <summary>Halts an agent as what watches the loss limits does: inside a transaction of its own, which it commits.</summary>
    private static async Task<bool> HaltAsync(AccountsApplication application, Guid userId, Guid agentId, AgentStateCause cause, DateTimeOffset at)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var halted = await application.AgentHalts.HaltAsync(context, userId, agentId, cause, at, TestContext.Current.CancellationToken);

        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        return halted;
    }
}
