using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Experience;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// An agent can opt in to automatic role reassignment by strength, applied at most once every 7
/// days and only when the agent has no open position. Each change is logged and notified
/// (FR-TEAM-10). See "Reassigning the roles automatically" in docs/agents.md.
/// </summary>
/// <remarks>
/// The application of these tests damps by 4 and takes 4 samples as enough, so that a slice of
/// 4 samples has half of its measure as its score. Its file has 5 days between two
/// reassignments of an agent, and allows the models of Claude only. No scheduler of a test runs
/// the job: a test runs it itself.
/// </remarks>
public class RoleReassignmentTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string TeamModel = StoredAgentRows.TeamModel;
    private const string Stronger = "claude-b";

    private const int Days = 5;

    private const string GivenTemplate =
        "Agent {AgentId} of user {UserId} was given the model {ModelId} for the role {Role} in place of {PreviousModelId}, by its strength score {Score} from {TradingMode} data";

    private const string NotGivenTemplate =
        "The role {Role} of agent {AgentId} of user {UserId} was not given the model {ModelId} by strength: {RoleProblem}";

    private const string RunTemplate = "{RoleCount} roles of {AgentCount} agents were reassigned by strength";

    private static readonly string[] Unchanged =
    [
        "MarketAnalyst: claude-test-model", "SentimentAnalyst: claude-test-model", "Strategist: claude-test-model", "RiskManager: claude-test-model",
    ];

    private static readonly string[] Reassigned =
    [
        "MarketAnalyst: claude-b", "SentimentAnalyst: claude-test-model", "Strategist: claude-b", "RiskManager: claude-test-model",
    ];

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public void The_roles_are_reassigned_every_hour()
    {
        using var application = Application(new TestClock(Morning));
        var job = application.RoleReassignment;

        Assert.Equal("role-reassignment", job.Name);
        Assert.Equal("every 01:00:00", job.Schedule.ToString());
        Assert.Equal(Morning.AddHours(1), job.Schedule.NextRun(Morning, Morning.AddSeconds(30)));
    }

    [Fact]
    public async Task An_agent_that_opted_in_is_given_its_recommended_models_while_it_runs_and_holds_nothing()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);

        clock.Set(Morning.AddMinutes(7));
        await application.ReassignRolesAsync();

        // The Market Analyst and the Strategist are given the model that scores above the one
        // they had. Nothing is measured for the Sentiment Analyst, and the Risk Manager has a
        // sample and no measure: both stay as they are (FR-EXP-4).
        Assert.Equal(Reassigned, StoredAgentRole.Told(application.Database, agent));

        // The days are counted from now. The agent runs on, in the state that it was in: a
        // change of a model is no change of a state, and applies from the next cycle (FR-AGT-8).
        var stored = StoredAgent.Named(application.Database, "Swing");

        Assert.Equal((true, Morning.AddMinutes(7), "Running"), (stored.ReassignsRoles, stored.RolesReassignedAt, stored.State));
        Assert.Single(StoredAgentStateChange.Of(application.Database, agent));

        // It is the service's doing, and no action of a user: the audit trail has nothing of it.
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));

        // A run that is done again finds the agent no longer due.
        await application.ReassignRolesAsync();

        Assert.Equal(2, (await NotificationsOfAsync(application, user.Id)).Count);
    }

    [Fact]
    public async Task Each_change_is_logged_and_its_user_is_told_with_which_data_was_used()
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);
        var team = await application.GiveModelAsync("claude", TeamModel, Morning);
        var stronger = await application.GiveModelAsync("claude", Stronger, Morning);

        await application.ReassignRolesAsync();

        // One notification for each role that was given another model, about the agent, in
        // plain words: the role, both models, both scores with the data that each is of
        // (FR-EXP-9), why, and from when. Both are raised in the same moment, so they are read
        // here in the order of what they say.
        var told = await NotificationsOfAsync(application, user.Id);

        Assert.All(told, notification => Assert.Equal((NotificationType.RolesReassigned, agent), (notification.Type, notification.AgentId)));
        Assert.Equal(
            [
                "The Market Analyst of the agent in BTCUSDT (Demo) now uses claude-b in place of claude-test-model: automatic reassignment " +
                "by strength is on for it. On Spot 1H, its strength score is +25 percentage points from 4 samples of Demo data, against " +
                "+12.5 percentage points from 4 samples of Demo data for the model before. It applies from the next cycle, and no role is " +
                "reassigned again for 5 days.",
                "The Strategist of the agent in BTCUSDT (Demo) now uses claude-b in place of claude-test-model: automatic reassignment by " +
                "strength is on for it. On Spot 1H, its strength score is +0.5 R from 4 samples of Demo data, against no strength score " +
                "yet for the model before. It applies from the next cycle, and no role is reassigned again for 5 days.",
            ],
            told.Select(notification => notification.Content).Order(StringComparer.Ordinal));

        // One event of the log for each, by ids, with the score and the data that it went by.
        var logged = application.Events().Where(written => written.Template == GivenTemplate).ToList();

        Assert.Equal(
            [
                (agent.ToString(), user.Id.ToString(), stronger.ToString(), "MarketAnalyst", team.ToString(), 0.25m, "Demo"),
                (agent.ToString(), user.Id.ToString(), stronger.ToString(), "Strategist", team.ToString(), 0.5m, "Demo"),
            ],
            logged.Select(written => (
                written.Text("AgentId"),
                written.Text("UserId"),
                written.Text("ModelId"),
                written.Text("Role"),
                written.Text("PreviousModelId"),
                written["Score"].GetDecimal(),
                written.Text("TradingMode"))));
        Assert.All(logged, written => Assert.Equal("Information", written.Level));

        // And the run says how much it did.
        var run = Assert.Single(application.Events(), written => written.Template == RunTemplate);

        Assert.Equal((2, 1), (run["RoleCount"].GetInt32(), run["AgentCount"].GetInt32()));
    }

    [Fact]
    public async Task The_page_of_the_agent_says_what_the_notification_is_about_and_when_its_roles_were_last_reassigned()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);

        await application.ReassignRolesAsync();

        // The page of the state of the agent has what the service tells its user.
        var state = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(agent)));

        Assert.Contains("Roles reassigned", state);
        Assert.Contains("The Strategist of the agent in BTCUSDT (Demo) now uses claude-b in place of claude-test-model", state);

        // And the form of its settings says when the service last did it, and until when it does not again.
        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        Assert.Contains($"<p role=\"note\">{AgentPages.RolesLastReassigned(Morning, Days)}</p>", form);
        Assert.Contains("on 17 November 2026, 09:20 UTC, and reassigns none again before 22 November 2026, 09:20 UTC.", form);
    }

    [Fact]
    public async Task An_agent_that_did_not_opt_in_keeps_its_team()
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id, reassignsRoles: false);

        await application.ReassignRolesAsync();

        // The same scores recommend the same models, and nothing gives them to its roles: an
        // agent opts in (FR-TEAM-10).
        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Null(StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
        Assert.Empty(await NotificationsOfAsync(application, user.Id));
    }

    [Theory]
    [InlineData(AgentState.Draft)]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.Halted)]
    [InlineData(AgentState.AttentionRequired)]
    [InlineData(AgentState.Stopped)]
    public async Task An_agent_that_does_not_run_keeps_its_team(AgentState state)
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id, state: state);

        await application.ReassignRolesAsync();

        // An agent that does not run stays as its user left it, and is asked about once it runs.
        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Null(StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
        Assert.Empty(await NotificationsOfAsync(application, user.Id));
    }

    [Fact]
    public async Task An_agent_with_an_open_position_keeps_its_team_until_it_holds_nothing()
    {
        var clock = new TestClock(Morning);
        var exposure = new StandInExposure();
        using var application = Application(clock, exposure);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);

        // Only when the agent has no open position (FR-TEAM-10): while it holds one, every run
        // leaves its team as it is, and counts no days.
        exposure.Holds(agent);
        await application.ReassignRolesAsync();

        clock.Set(Morning.AddHours(1));
        await application.ReassignRolesAsync();

        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Null(StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
        Assert.Empty(await NotificationsOfAsync(application, user.Id));

        // The run after its position is closed gives its roles their models.
        exposure.HoldsNothing(agent);
        clock.Set(Morning.AddHours(2));
        await application.ReassignRolesAsync();

        Assert.Equal(Reassigned, StoredAgentRole.Told(application.Database, agent));
        Assert.Equal(Morning.AddHours(2), StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
    }

    [Fact]
    public async Task A_position_that_opens_while_the_scores_are_worked_out_keeps_the_team()
    {
        var exposure = new StandInExposure();
        using var application = Application(new TestClock(Morning), exposure);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);

        // The agent holds nothing when the run first asks, with no lock. By the time that the
        // run asks again, under the lock of the account, an entry of the agent was written.
        var asked = 0;
        exposure.OnPositionAsked = held =>
        {
            if (++asked == 2)
            {
                exposure.Holds(held);
            }

            return Task.CompletedTask;
        };

        await application.ReassignRolesAsync();

        // What counts is what holds under the lock, which what writes an entry takes too.
        Assert.Equal(2, asked);
        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Null(StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
        Assert.Empty(await NotificationsOfAsync(application, user.Id));
    }

    [Theory]
    [InlineData("reassigns_roles = false")]
    [InlineData("state = 'Paused'")]
    [InlineData("timeframe = 'FourHours'")]
    [InlineData("roles_reassigned_at = now()")]
    public async Task What_its_user_changed_while_the_scores_were_worked_out_is_seen_under_the_lock(string change)
    {
        var exposure = new StandInExposure();
        using var application = Application(new TestClock(Morning), exposure);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);

        // The run read the agent as one that is due, with no lock. Before it takes the lock of
        // the account, the agent is turned off, paused, given another timeframe, or reassigned.
        var asked = 0;
        exposure.OnPositionAsked = _ =>
        {
            if (++asked == 1)
            {
                application.Database.Execute($"UPDATE agents SET {change} WHERE id = '{agent}'");
            }

            return Task.CompletedTask;
        };

        await application.ReassignRolesAsync();

        // The agent is read again under the lock, and what it is then is gone by: nothing is
        // decided for an agent as it was a moment before.
        Assert.Equal(1, asked);
        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Empty(await NotificationsOfAsync(application, user.Id));
    }

    [Fact]
    public async Task A_position_that_is_stored_as_open_keeps_the_team_of_its_agent()
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);

        // What keeps the positions is asked, and no stand-in: the agent has a position that is
        // not closed.
        await application.GiveTradeAsync(user.Id, agent, Morning, closed: false);

        await application.ReassignRolesAsync();

        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Null(StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
    }

    [Fact]
    public async Task The_roles_of_an_agent_are_reassigned_at_most_once_in_the_days_of_the_configuration_file()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);

        await application.ReassignRolesAsync();

        Assert.Equal(Reassigned, StoredAgentRole.Told(application.Database, agent));

        // A third model comes to score above the one that the Market Analyst has now. The file
        // of the tests has 5 days: a run within them changes nothing, however often it runs.
        var third = await application.GiveModelAsync("claude", "claude-c", Morning);
        await application.LetKeyUseAsync(user.Id, "claude", "claude-c", Morning);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, third, Morning.AddHours(1), matched: 12, missed: 0);

        clock.Set(Morning.AddDays(Days).AddMinutes(-1));
        await application.ReassignRolesAsync();

        Assert.Equal(Reassigned, StoredAgentRole.Told(application.Database, agent));
        Assert.Equal(Morning, StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);

        // Once they are over, the next run gives the Market Analyst the third model, and the
        // days are counted anew.
        clock.Set(Morning.AddDays(Days));
        await application.ReassignRolesAsync();

        Assert.Equal("MarketAnalyst: claude-c", StoredAgentRole.Told(application.Database, agent)[0]);
        Assert.Equal(Morning.AddDays(Days), StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
        Assert.Equal(3, (await NotificationsOfAsync(application, user.Id)).Count);
    }

    [Fact]
    public async Task A_run_that_changes_nothing_counts_no_days_and_tells_nobody()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        var user = await application.RegisterVerifiedAsync(Ada);

        // The agent opted in, and nothing is measured yet: no role is recommended anything.
        var agent = await application.GiveAgentAsync(
            user.Id, Morning.AddDays(-2), name: "Swing", state: AgentState.Running, reassignsRoles: true);
        var stronger = await application.GiveModelAsync("claude", Stronger, Morning);
        await application.LetKeyUseAsync(user.Id, "claude", Stronger, Morning);

        await application.ReassignRolesAsync();

        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Null(StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
        Assert.Empty(await NotificationsOfAsync(application, user.Id));
        Assert.DoesNotContain(application.Events(), written => written.Template == RunTemplate);

        // So the first run after something is measured changes the role: no days were counted
        // from a run that changed nothing.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, stronger, Morning.AddMinutes(30), matched: 4, missed: 0);

        clock.Set(Morning.AddHours(1));
        await application.ReassignRolesAsync();

        Assert.Equal("MarketAnalyst: claude-b", StoredAgentRole.Told(application.Database, agent)[0]);
        Assert.Equal(Morning.AddHours(1), StoredAgent.Named(application.Database, "Swing").RolesReassignedAt);
    }

    [Fact]
    public async Task A_candidate_that_does_not_score_above_the_model_that_a_role_has_is_not_given()
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(
            user.Id, Morning.AddDays(-2), name: "Swing", state: AgentState.Running, reassignsRoles: true);
        var stronger = await application.GiveModelAsync("claude", Stronger, Morning);
        await application.LetKeyUseAsync(user.Id, "claude", Stronger, Morning);

        // The only model that is measured was right once in 4 calls, which is below 0, and the
        // model that the role has is not measured, which counts as 0: the service never gives a
        // role a model that is measured as weaker than nothing.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, stronger, Morning.AddHours(-5), matched: 1, missed: 3);

        await application.ReassignRolesAsync();

        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Empty(await NotificationsOfAsync(application, user.Id));
    }

    [Fact]
    public async Task A_model_that_the_key_can_no_longer_use_by_the_time_of_the_lock_is_not_given()
    {
        var exposure = new StandInExposure();
        using var application = Application(new TestClock(Morning), exposure);
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);
        var third = await application.GiveModelAsync("claude", "claude-c", Morning);
        await application.LetKeyUseAsync(user.Id, "claude", "claude-c", Morning);

        // The third model scores highest as the Market Analyst, so it is what that role is
        // recommended, and the Strategist is recommended the second.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, third, Morning.AddHours(-3), matched: 12, missed: 0);

        // The provider stops listing the third model to her key after the scores were worked
        // out, and before the roles are stored: the second time that the position is asked
        // about is under the lock of the account.
        var asked = 0;
        exposure.OnPositionAsked = async _ =>
        {
            if (++asked == 2)
            {
                await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

                await context.Set<AiCredential>()
                    .Where(credential => credential.UserId == user.Id)
                    .ExecuteUpdateAsync(
                        row => row.SetProperty(credential => credential.AccessibleModels, new[] { TeamModel, Stronger }), Cancellation);
            }
        };

        await application.ReassignRolesAsync();

        // The role is held to what its user can select now, as a form is (FR-CAT-9): the Market
        // Analyst stays as it is, and the Strategist is given its model all the same.
        Assert.Equal(
            ["MarketAnalyst: claude-test-model", "SentimentAnalyst: claude-test-model", "Strategist: claude-b", "RiskManager: claude-test-model"],
            StoredAgentRole.Told(application.Database, agent));

        var refused = Assert.Single(application.Events(), written => written.Template == NotGivenTemplate);

        Assert.Equal(
            ("MarketAnalyst", agent.ToString(), third.ToString(), "NotListed"),
            (refused.Text("Role"), refused.Text("AgentId"), refused.Text("ModelId"), refused.Text("RoleProblem")));
        Assert.Equal(
            [NotificationType.RolesReassigned],
            (await NotificationsOfAsync(application, user.Id)).Select(notification => notification.Type));
    }

    [Fact]
    public async Task An_agent_in_Live_is_reassigned_without_being_asked_once_more_and_is_told_which_data_was_used()
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id, mode: TradingMode.Live);

        // Turning it on was asked about (FR-UI-3). What the service does then asks nobody.
        await application.ReassignRolesAsync();

        Assert.Equal(Reassigned, StoredAgentRole.Told(application.Database, agent));

        var told = await NotificationsOfAsync(application, user.Id);

        Assert.Equal(2, told.Count);
        Assert.Contains(
            told,
            notification => notification.Content.StartsWith(
                "The Market Analyst of the agent in BTCUSDT (Live) now uses claude-b in place of claude-test-model: automatic reassignment " +
                "by strength is on for it. On Spot 1H, its strength score is +25 percentage points from 4 samples of Live data, against " +
                "+12.5 percentage points from 4 samples of Live data for the model before.",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_user_is_told_when_the_reassignment_gives_the_Strategist_the_provider_of_the_Risk_Manager()
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id);

        // The Strategist has a model of Gemini, and the Risk Manager one of Claude.
        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, provider: "gemini", model: "gemini-a");

        await application.ReassignRolesAsync();

        // Strength gives the Strategist a model of Claude: the one reviews the plan of the
        // other, on one provider now, and its user is told so (FR-TEAM-5).
        Assert.Equal("Strategist: claude-b", StoredAgentRole.Told(application.Database, agent)[2]);
        Assert.All(
            await NotificationsOfAsync(application, user.Id),
            notification => Assert.Contains(" The Strategist and the Risk Manager now use the same provider. It applies", notification.Content));
    }

    [Fact]
    public async Task A_notification_stays_within_its_length_whatever_a_model_is_called()
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(
            user.Id, Morning.AddDays(-2), name: "Swing", state: AgentState.Running, team: false, reassignsRoles: true);

        // A provider gives its models names of any length.
        var has = await application.GiveModelAsync("claude", "claude-long-a", Morning, displayName: new string('a', 300));
        var stronger = await application.GiveModelAsync("claude", "claude-long-b", Morning, displayName: new string('b', 300));

        foreach (var role in Enum.GetValues<Role>())
        {
            await application.GiveRoleAsync(user.Id, agent, role, Morning, model: "claude-long-a");
        }

        await application.LetKeyUseAsync(user.Id, "claude", "claude-long-b", Morning);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, stronger, Morning.AddHours(-5), matched: 4, missed: 0);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, has, Morning.AddHours(-30), matched: 3, missed: 1);

        await application.ReassignRolesAsync();

        // Each name is cut, and says that it is: the notification is stored, and says the rest.
        var told = Assert.Single(await NotificationsOfAsync(application, user.Id));

        Assert.InRange(told.Content.Length, 1, Notification.ContentLength);
        Assert.Contains($"now uses {new string('b', 39)}… in place of {new string('a', 39)}…: automatic", told.Content);
        Assert.EndsWith("no role is reassigned again for 5 days.", told.Content);
    }

    [Fact]
    public async Task A_notification_that_says_the_most_cuts_the_names_further_and_no_sentence()
    {
        using var application = Application(new TestClock(Morning));
        var user = await application.RegisterVerifiedAsync(Ada);
        var agent = await application.GiveAgentAsync(
            user.Id, Morning.AddDays(-2), name: "Swing", state: AgentState.Running, reassignsRoles: true);

        // The Strategist has a model of Gemini, which made 0.2 R on each of 4 plans. A model of
        // Claude made 1 R on each of 4. Both have names of 300 characters.
        var has = await application.GiveModelAsync("gemini", "gemini-long", Morning, displayName: new string('g', 300));
        var stronger = await application.GiveModelAsync("claude", "claude-long", Morning, displayName: new string('c', 300));
        var reviews = await application.GiveModelAsync("claude", TeamModel, Morning);

        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, provider: "gemini", model: "gemini-long");
        await application.LetKeyUseAsync(user.Id, "claude", "claude-long", Morning);

        foreach (var hour in Enumerable.Range(0, 4))
        {
            await application.GiveTradeAsync(
                user.Id, agent, Morning.AddHours(-20 + hour), resultInR: 0.2m, roles: [(Role.Strategist, has), (Role.RiskManager, reviews)]);
            await application.GiveTradeAsync(
                user.Id, agent, Morning.AddHours(-10 + hour), resultInR: 1m, roles: [(Role.Strategist, stronger), (Role.RiskManager, reviews)]);
        }

        await application.ReassignRolesAsync();

        // It says both scores, and that the Strategist has the provider of the Risk Manager
        // now: with names of 40 characters that is more than a notification holds. The names
        // are cut further, and every sentence is whole.
        var told = Assert.Single(await NotificationsOfAsync(application, user.Id));

        Assert.InRange(told.Content.Length, 1, Notification.ContentLength);
        Assert.StartsWith("The Strategist of the agent in BTCUSDT (Demo) now uses ccc", told.Content);
        Assert.Contains("… in place of ggg", told.Content);
        Assert.Contains(
            "its strength score is +0.5 R from 4 samples of Demo data, against +0.1 R from 4 samples of Demo data for the model before. " +
            "The Strategist and the Risk Manager now use the same provider. " +
            "It applies from the next cycle, and no role is reassigned again for 5 days.",
            told.Content);
        Assert.EndsWith("for 5 days.", told.Content);
    }

    [Fact]
    public async Task What_another_users_agents_did_reassigns_nothing()
    {
        using var application = Application(new TestClock(Morning));
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);

        // Both opted in, on the same market and timeframe, and one run asks about both: Grace's
        // agent first, which is the older. Her agents measured a model. Ada's key can use the
        // same model, and her agents measured nothing.
        var hers = await MeasuredAsync(application, grace.Id);
        var agent = await application.GiveAgentAsync(
            ada.Id, Morning.AddDays(-1), name: "Mine", state: AgentState.Running, reassignsRoles: true);
        await application.LetKeyUseAsync(ada.Id, "claude", Stronger, Morning);

        await application.ReassignRolesAsync();

        // Grace's agent is given what her own agents measured. Nothing of it reassigns a role
        // of Ada's agent, and nobody is told of the agent of another (NFR-SEC-3).
        Assert.Equal(Reassigned, StoredAgentRole.Told(application.Database, hers));
        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));
        Assert.Null(StoredAgent.Named(application.Database, "Mine").RolesReassignedAt);
        Assert.Empty(await NotificationsOfAsync(application, ada.Id));
        Assert.All(await NotificationsOfAsync(application, grace.Id), told => Assert.Equal(hers, told.AgentId));
        Assert.Equal(2, (await NotificationsOfAsync(application, grace.Id)).Count);
    }

    [Fact]
    public async Task A_user_turns_the_automatic_reassignment_on_and_off_on_the_form_of_the_agent()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The form has the box, which is not ticked for its user: an agent opts in (FR-TEAM-10).
        // Under it the form says what the service does then, with the days of the file.
        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.NewAgentPage));

        Assert.False(AgentBrowser.ReassignsOn(form));
        Assert.Contains($"<label for=\"agent-reassigns-roles\">{AgentPages.ReassignsRolesLabel}</label>", form);
        Assert.Contains(AgentPages.ReassignsRolesExplained(Days), form);
        Assert.Contains("at most once in 5 days for the agent, only while the agent runs, and only while it has no open position", form);

        // An agent that is created without the box has it off, and one that is created with it has it on.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Off"));
        await client.CreateAgentAsync([.. AgentBrowser.Entry(name: "On", symbol: "SOLUSDT"), AgentBrowser.Reassigns]);

        var agent = StoredAgent.Named(application.Database, "On");

        Assert.False(StoredAgent.Named(application.Database, "Off").ReassignsRoles);
        Assert.Equal((true, null), (agent.ReassignsRoles, agent.RolesReassignedAt));
        Assert.True(AgentBrowser.ReassignsOn(await client.PageAsync(AgentBrowser.AgentPage(agent.Id))));

        // A button that stores nothing keeps the box as it was entered.
        var (_, shown) = await client.SaveAgentAsync(agent.Id, [.. AgentBrowser.Entry(name: "On", symbol: "SOLUSDT"), ("Show", "models")]);

        Assert.False(AgentBrowser.ReassignsOn(shown));
        Assert.True(StoredAgent.Named(application.Database, "On").ReassignsRoles);

        // A form that is saved without the box turns it off.
        var (path, _) = await client.SaveAgentAsync(agent.Id, AgentBrowser.Entry(name: "On", symbol: "SOLUSDT"));

        Assert.Equal(AgentBrowser.AgentsPage, path);
        Assert.False(StoredAgent.Named(application.Database, "On").ReassignsRoles);
        Assert.False(AgentBrowser.ReassignsOn(await client.PageAsync(AgentBrowser.AgentPage(agent.Id))));
    }

    [Fact]
    public async Task A_copy_of_an_agent_starts_with_the_automatic_reassignment_off()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(
            user.Id, Morning.AddDays(-9), name: "Swing", reassignsRoles: true, rolesReassignedAt: Morning.AddDays(-8));

        await client.CloneAgentAsync(agent);

        // A copy is a new agent: its user opts in for it, and the service has reassigned none
        // of its roles. The agent that it was made from is as it was.
        var copy = StoredAgent.Named(application.Database, "Copy of Swing");

        Assert.Equal((false, null), (copy.ReassignsRoles, copy.RolesReassignedAt));
        Assert.Equal(
            (true, Morning.AddDays(-8)),
            (StoredAgent.Named(application.Database, "Swing").ReassignsRoles, StoredAgent.Named(application.Database, "Swing").RolesReassignedAt));
    }

    [Fact]
    public async Task Turning_the_automatic_reassignment_on_or_off_for_an_agent_in_Live_asks_once_more()
    {
        using var application = Application(new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", state: AgentState.Running, mode: TradingMode.Live);
        var team = Assert.Single(StoredAgentRole.Of(application.Database, agent).Select(role => role.ModelId).Distinct());
        var entry = AgentBrowser.Entry(name: "Swing").WithTeam("claude", team);

        // Turning it on has the service change which models trade with real money, and ask
        // nobody again: nothing is stored, and the form asks once more, with the box as it was
        // ticked (FR-UI-3).
        var (path, asks) = await client.SaveAgentAsync(agent, [.. entry, AgentBrowser.Reassigns]);

        Assert.Equal(AgentBrowser.AgentPage(agent), path);
        Assert.Contains($"<p class=\"form-warning\" role=\"alert\" tabindex=\"-1\" autofocus>{AgentPages.ConfirmSaveForLive}</p>", AgentBrowser.Read(asks));
        Assert.Contains("whether the roles are reassigned automatically", AgentPages.ConfirmSaveForLive);
        Assert.True(AgentBrowser.ReassignsOn(asks));
        Assert.False(StoredAgent.Named(application.Database, "Swing").ReassignsRoles);

        // The button that says so once more stores it.
        var (saved, _) = await client.SaveAgentAsync(agent, [.. entry, AgentBrowser.Reassigns, ("Confirm", "live")]);

        Assert.Equal(AgentBrowser.AgentsPage, saved);
        Assert.True(StoredAgent.Named(application.Database, "Swing").ReassignsRoles);

        // A form that brings it as it is asks nothing. And turning it off is asked about as well.
        var (unchanged, _) = await client.SaveAgentAsync(agent, [.. AgentBrowser.Entry(name: "Swing again").WithTeam("claude", team), AgentBrowser.Reassigns]);
        var (off, _) = await client.SaveAgentAsync(agent, AgentBrowser.Entry(name: "Swing again").WithTeam("claude", team));

        Assert.Equal(AgentBrowser.AgentsPage, unchanged);
        Assert.Equal(AgentBrowser.AgentPage(agent), off);
        Assert.True(StoredAgent.Named(application.Database, "Swing again").ReassignsRoles);
    }

    [Fact]
    public async Task The_page_that_switches_an_agent_to_Live_says_that_its_roles_are_reassigned_automatically()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // She proves who she is, and stores a Live key, as the switch takes.
        await client.StoreLiveKeyAsync();
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Plain"));
        await client.CreateAgentAsync([.. AgentBrowser.Entry(name: "Reassigning", symbol: "SOLUSDT"), AgentBrowser.Reassigns]);

        // Said where the switch is confirmed: in Live the service changes the models that trade
        // with real money, and asks nobody (FR-UI-3). An agent that did not opt in is told nothing of it.
        var reassigning = AgentBrowser.Read(
            await client.PageAsync(AgentBrowser.SwitchToLivePage(StoredAgent.Named(application.Database, "Reassigning").Id)));
        var plain = AgentBrowser.Read(
            await client.PageAsync(AgentBrowser.SwitchToLivePage(StoredAgent.Named(application.Database, "Plain").Id)));

        Assert.Contains(AgentPages.ReassignsRolesInLive(Days), reassigning);
        Assert.Contains("at most once in 5 days, and does not ask you again", reassigning);
        Assert.DoesNotContain("Automatic reassignment", plain);
    }

    [Fact]
    public async Task Turning_the_reassignment_off_and_on_again_does_not_start_the_days_anew()
    {
        var clock = new TestClock(Morning);
        using var application = Application(clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await MeasuredAsync(application, user.Id, rolesReassignedAt: Morning.AddDays(-1));
        var team = Assert.Single(StoredAgentRole.Of(application.Database, agent).Select(role => role.ModelId).Distinct());
        var entry = AgentBrowser.Entry(name: "Swing").WithTeam("claude", team);

        // The service reassigned a role of the agent a day ago. Its user turns it off, and on again.
        await client.SaveAgentAsync(agent, entry);
        await client.SaveAgentAsync(agent, [.. entry, AgentBrowser.Reassigns]);

        Assert.Equal((true, Morning.AddDays(-1)), (StoredAgent.Named(application.Database, "Swing").ReassignsRoles, StoredAgent.Named(application.Database, "Swing").RolesReassignedAt));

        // The days are counted from the last reassignment still: at most once in them, whatever
        // was pressed meanwhile (FR-TEAM-10).
        await application.ReassignRolesAsync();

        Assert.Equal(Unchanged, StoredAgentRole.Told(application.Database, agent));

        clock.Set(Morning.AddDays(Days - 1));
        await application.ReassignRolesAsync();

        Assert.Equal(Reassigned, StoredAgentRole.Told(application.Database, agent));
    }

    /// <summary>
    /// Gives a user a running agent named Swing on Spot, on the hour, that opted in, whose roles
    /// all have the model of the tests, and what its roles are measured by: as the Market
    /// Analyst, another model of Claude was right 4 times of 4 and the model of the tests 3
    /// times of 4, and as the Strategist the other model made 1 R on each of 4 plans, which the
    /// model of the tests approved as the Risk Manager. The key of the user can use the other
    /// model. A test says what is otherwise.
    /// </summary>
    /// <returns>The id of the agent.</returns>
    private static async Task<Guid> MeasuredAsync(
        AccountsApplication application,
        Guid userId,
        AgentState state = AgentState.Running,
        TradingMode mode = TradingMode.Demo,
        bool reassignsRoles = true,
        DateTimeOffset? rolesReassignedAt = null)
    {
        var agent = await application.GiveAgentAsync(
            userId, Morning.AddDays(-2), name: "Swing", state: state, mode: mode, reassignsRoles: reassignsRoles, rolesReassignedAt: rolesReassignedAt);
        var team = await application.GiveModelAsync("claude", TeamModel, Morning);
        var stronger = await application.GiveModelAsync("claude", Stronger, Morning);

        await application.LetKeyUseAsync(userId, "claude", Stronger, Morning);

        await application.GiveCallsAsync(userId, agent, Role.MarketAnalyst, stronger, Morning.AddHours(-5), matched: 4, missed: 0);
        await application.GiveCallsAsync(userId, agent, Role.MarketAnalyst, team, Morning.AddHours(-30), matched: 3, missed: 1);

        foreach (var hour in Enumerable.Range(0, 4))
        {
            await application.GiveTradeAsync(
                userId, agent, Morning.AddHours(-10 + hour), resultInR: 1m, roles: [(Role.Strategist, stronger), (Role.RiskManager, team)]);
        }

        return agent;
    }

    /// <summary>The notifications of a user as they are stored, in the order they were raised in.</summary>
    private static async Task<IReadOnlyList<Notification>> NotificationsOfAsync(AccountsApplication application, Guid userId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<Notification>()
            .AsNoTracking()
            .Where(notification => notification.UserId == userId)
            .OrderBy(notification => notification.CreatedAt)
            .ThenBy(notification => notification.Id)
            .ToListAsync(Cancellation);
    }

    /// <summary>The application, with a minimum sample and a damping that a test reaches with a few rows.</summary>
    private static AccountsApplication Application(TestClock clock, StandInExposure? exposure = null) =>
        AgentsApplication.With(
            new StandInMarketData(),
            clock,
            exposure,
            settings: file => file.With("Experience:MinSamples", 4).With("Experience:Damping", 4));
}
