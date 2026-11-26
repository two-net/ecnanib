using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Notifications;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// What becomes of the agents of an account that an operator disables (FR-OPS-4): the ones that
/// run move to Attention required, in the transaction that disables the account, and wait for
/// their user. An agent that a loss limit halted stays halted, and its daily halt does not end
/// while the account is disabled. Nothing of a disabled account starts to run. See "When an
/// account is disabled" in docs/agents.md.
/// </summary>
public class AgentAccountDependentsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";
    private const string Olive = "olive@example.com";

    private const string DisableForm = "disable-account";
    private const string EnableForm = "enable-account";

    private const string WaitsTemplate = "Agent {AgentId} of user {UserId} requires attention: an operator disabled the account of its user";

    private const string StaysHaltedTemplate =
        "Agent {AgentId} of user {UserId} stays halted after the day of its daily loss limit: the account of its user is disabled";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextDay = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_agents_of_a_disabled_account_that_run_wait_for_its_user_and_every_other_agent_stays_as_it_is()
    {
        var clock = new TestClock(Morning.AddHours(1));
        using var application = Application(clock);
        using var anOperator = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await anOperator.SignInAsync(Olive);

        // Two of hers run, one of them in Live. The others do not: each was left as it is by
        // its user or by the service.
        var runs = await application.GiveAgentAsync(ada.Id, Morning, name: "Runs", state: AgentState.Running, cause: AgentStateCause.UserStarted);
        var runsInLive = await application.GiveAgentAsync(
            ada.Id, Morning, name: "Runs in Live", state: AgentState.Running, symbol: "ETHUSDT", mode: TradingMode.Live, cause: AgentStateCause.UserStarted);
        var halted = await application.GiveAgentAsync(
            ada.Id, Morning, name: "Halted", state: AgentState.Halted, symbol: "SOLUSDT", cause: AgentStateCause.DrawdownLimitReached);
        var paused = await application.GiveAgentAsync(
            ada.Id, Morning, name: "Paused", state: AgentState.Paused, symbol: "BNBUSDT", cause: AgentStateCause.UserPaused);
        var waiting = await application.GiveAgentAsync(
            ada.Id, Morning, name: "Waiting", state: AgentState.AttentionRequired, symbol: "XRPUSDT", cause: AgentStateCause.AiKeyInvalid);
        var stopped = await application.GiveAgentAsync(
            ada.Id, Morning, name: "Stopped", state: AgentState.Stopped, symbol: "ADAUSDT", cause: AgentStateCause.UserStopped);
        var draft = await application.GiveAgentAsync(ada.Id, Morning, name: "Draft", symbol: "DOGEUSDT");

        // And one of another user runs.
        var hers = await application.GiveAgentAsync(grace.Id, Morning, name: "Hers", state: AgentState.Running, cause: AgentStateCause.UserStarted);

        var before = StoredAgent.All(application.Database).ToDictionary(agent => agent.Id);
        var changesBefore = StoredAgentStateChange.All(application.Database).Count;

        await anOperator.SubmitAsync(WebUiModule.OperatorAccountPath(ada.Id), DisableForm);

        // The two that ran wait for her, since the moment that the account was disabled, each
        // with a change of its state that says why, in the mode that the agent is in (FR-AGT-10,
        // FR-BIN-11).
        Assert.Equal(("AttentionRequired", "AccountDisabled", Morning.AddHours(1)), AgentControlTests.StateOf(application, runs));
        Assert.Equal(("AttentionRequired", "AccountDisabled", Morning.AddHours(1)), AgentControlTests.StateOf(application, runsInLive));
        Assert.Equal(
            [("Demo", "AttentionRequired", "AccountDisabled"), ("Live", "AttentionRequired", "AccountDisabled")],
            StoredAgentStateChange.All(application.Database).Skip(changesBefore).Select(change => (change.Mode, change.State, change.Cause)).Order());

        // Every other agent is as it was, hers and anyone else's.
        Assert.All(
            new[] { halted, paused, waiting, stopped, draft, hers },
            agent => Assert.Equal(before[agent], StoredAgent.All(application.Database).Single(stored => stored.Id == agent)));

        // She is told of each agent that was moved, in the transaction that moved it, and
        // nobody else is told anything (FR-NTF-1).
        Assert.Equal(
            new (NotificationType, Guid?, string)[]
            {
                (
                    NotificationType.AttentionRequired,
                    runs,
                    "The agent in BTCUSDT (Demo) waits for you: an operator of the service disabled your account. An open position " +
                    "stays open with its stop-loss order. Once your account is enabled again, sign in and resume the agent."),
                (
                    NotificationType.AttentionRequired,
                    runsInLive,
                    "The agent in ETHUSDT (Live) waits for you: an operator of the service disabled your account. An open position " +
                    "stays open with its stop-loss order. Once your account is enabled again, sign in and resume the agent."),
            }.OrderBy(told => told.Item2),
            (await application.ToldAsync(ada.Id)).OrderBy(told => told.AgentId));
        Assert.Empty(await application.ToldAsync(grace.Id));

        // It is the doing of the operator to the account, and no action of a user on an agent:
        // the audit trail has the one event of the account, and none of an agent.
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), audited => audited.Action.StartsWith("Agent", StringComparison.Ordinal));

        // The log says each agent, by its id and that of its user.
        Assert.Equal(
            new[] { runs.ToString(), runsInLive.ToString() }.Order(),
            application.Events().Where(written => written.Template == WaitsTemplate).Select(written => written.Text("AgentId")!).Order());

        // What says which agents run cycles says neither of hers any more.
        var running = await application.RunningAgents.OfTimeframesAsync(Enum.GetValues<Timeframe>(), TestContext.Current.CancellationToken);

        Assert.Equal([hers], running.Select(agent => agent.Agent.Id));
    }

    [Fact]
    public async Task Enabling_the_account_moves_no_agent_and_its_user_resumes_the_ones_that_waited()
    {
        var clock = new TestClock(Morning.AddHours(1));
        using var application = Application(clock);
        using var anOperator = application.CreateClient();
        using var user = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(ada.Id, Morning);
        await anOperator.SignInAsync(Olive);

        var agent = await application.GiveAgentAsync(ada.Id, Morning, state: AgentState.Running, cause: AgentStateCause.UserStarted);
        var account = WebUiModule.OperatorAccountPath(ada.Id);

        // A few minutes apart, so that the session of the operator has not ended for inactivity.
        await anOperator.SubmitAsync(account, DisableForm);
        clock.Set(Morning.AddHours(1).AddMinutes(5));
        await anOperator.SubmitAsync(account, EnableForm);

        // Nothing was started for her: the agent waits as it did.
        Assert.Equal(("AttentionRequired", "AccountDisabled", Morning.AddHours(1)), AgentControlTests.StateOf(application, agent));

        // She signs in, reads why it waits, and resumes it.
        Assert.Equal("/", (await user.SignInAsync(Ada)).Path);
        var page = await user.PageAsync(AgentBrowser.AgentStatePage(agent));

        Assert.Contains($"<p>{AgentPages.Says(AgentStateCause.AccountDisabled)}</p>", page);
        Assert.Contains("<button type=\"submit\">Resume the agent</button>", page);

        clock.Set(Morning.AddHours(1).AddMinutes(10));
        Assert.Equal(AgentBrowser.AgentStatePage(agent), (await user.ResumeAgentAsync(agent)).Path);
        Assert.Equal(("Running", "UserResumed", Morning.AddHours(1).AddMinutes(10)), AgentControlTests.StateOf(application, agent));
    }

    [Fact]
    public async Task A_daily_halt_does_not_end_while_the_account_is_disabled_and_ends_once_it_is_enabled()
    {
        // The UTC day after the halt has begun: the service would resume the agent now.
        var clock = new TestClock(NextDay.AddSeconds(2));
        using var application = Application(clock);
        using var anOperator = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(ada.Id, Morning);
        await anOperator.SignInAsync(Olive);

        var agent = await application.GiveAgentAsync(
            ada.Id, Morning, state: AgentState.Halted, cause: AgentStateCause.DailyLossLimitReached);
        var account = WebUiModule.OperatorAccountPath(ada.Id);

        await anOperator.SubmitAsync(account, DisableForm);

        // The agent stays halted when its account is disabled, so that the service goes on
        // closing its position where that failed. And its halt does not end: nothing of a
        // disabled account starts to run, by itself either.
        Assert.Equal(("Halted", "DailyLossLimitReached", Morning), AgentControlTests.StateOf(application, agent));
        Assert.Equal(0, await application.AgentHalts.EndDailyHaltsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("Halted", "DailyLossLimitReached", Morning), AgentControlTests.StateOf(application, agent));

        var stays = Assert.Single(application.Events(), written => written.Template == StaysHaltedTemplate);
        Assert.Equal(("Information", agent.ToString(), ada.Id.ToString()), (stays.Level, stays.Text("AgentId"), stays.Text("UserId")));

        // She cannot sign in to do anything about it, so she is told nothing of it.
        Assert.Empty(await application.ToldAsync(ada.Id));

        // The first close after the account is enabled ends the halt, as it ends any.
        await anOperator.SubmitAsync(account, EnableForm);
        clock.Set(NextDay.AddMinutes(15));

        Assert.Equal(1, await application.AgentHalts.EndDailyHaltsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("Running", "DailyLossHaltEnded", NextDay.AddMinutes(15)), AgentControlTests.StateOf(application, agent));
    }

    [Fact]
    public async Task A_resume_that_was_signed_in_before_its_account_was_disabled_runs_no_agent()
    {
        // A request of the user that is signed in, and reaches the lock of the account only
        // after an operator disabled it: the account is disabled between the two, as a request
        // that waited for the lock finds it. A request beside it, which nothing came before,
        // shows that the agent would have run.
        const string late = "/resumes-after-the-account-was-disabled";
        const string inTime = "/resumes-in-time";

        var clock = new TestClock(Morning.AddHours(1));
        using var application = Application(clock, host: host => host.HandleAtTheEnd(async request =>
        {
            var isLate = request.Request.Path.StartsWithSegments(late);

            if (!isLate && !request.Request.Path.StartsWithSegments(inTime))
            {
                return false;
            }

            var agentId = Guid.Parse(request.Request.Query["agent"]!);

            if (isLate)
            {
                var contexts = request.RequestServices.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Ecnanib.Platform.Persistence.EcnanibDbContext>>();
                await using var context = await contexts.CreateDbContextAsync(request.RequestAborted);
                var userId = SignedInUser.RequiredIdOf(request);

                await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlAsync(
                    context.Database, $"UPDATE users SET disabled_at = now() WHERE id = {userId}", request.RequestAborted);
            }

            var result = await request.RequestServices.GetRequiredService<IAgentControls>().ResumeAsync(request, agentId, confirmedForLive: false);
            await request.Response.WriteAsync(result.Outcome.ToString());

            return true;
        }));
        using var user = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(ada.Id, Morning);
        await user.SignInAsync(Ada);

        var first = await application.GiveAgentAsync(ada.Id, Morning, name: "First", state: AgentState.Paused, cause: AgentStateCause.UserPaused);
        var second = await application.GiveAgentAsync(
            ada.Id, Morning, name: "Second", state: AgentState.Paused, symbol: "ETHUSDT", cause: AgentStateCause.UserPaused);

        // In time, the control does what it is for.
        Assert.Equal("Done", await user.PageAsync($"{inTime}?agent={first}"));
        Assert.Equal("Running", AgentControlTests.StateOf(application, first).State);

        // After the account was disabled, it is answered as for an account that is gone, and
        // the agent stays as its user left it.
        Assert.Equal("NotFound", await user.PageAsync($"{late}?agent={second}"));
        Assert.Equal(("Paused", "UserPaused", Morning), AgentControlTests.StateOf(application, second));
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database), audited => audited.Action == "AgentResumed" && audited.Target == $"agent:{second}");
    }

    /// <summary>The application with one operator in its configuration file.</summary>
    private static AccountsApplication Application(TestClock clock, Action<IWebHostBuilder>? host = null) =>
        AgentsApplication.With(
            new StandInMarketData(), clock, settings: file => file.WithJson("Application:Operators", $"[ \"{Olive}\" ]"), host: host);
}
