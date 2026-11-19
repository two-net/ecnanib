using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages.Agents;
using KeyPages = Ecnanib.WebUi.Pages.Account.KeyPages;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// A user starts, pauses, resumes, and stops an agent (FR-AGT-1), and the agent follows the
/// states of the specification (SRS 3.3). Every change of its state records its cause and its
/// time, and is shown to the user (FR-AGT-10). A Binance key that is missing, is not valid, or
/// lacks the permission of the agent's market blocks a start (FR-KEY-5), and so does another
/// active agent for the same credential, market, and symbol (FR-AGT-9).
/// </summary>
public class AgentControlTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string MovedTemplate =
        "User {UserId} moved agent {AgentId} from {PreviousAgentState} to {AgentState}: {AgentStateCause}";

    private const string StoppedTemplate = "User {UserId} stopped agent {AgentId}, which was {AgentState}: {AgentStateCause}";

    private const string StartButton = "<button type=\"submit\">Start the agent</button>";
    private const string PauseButton = "<button type=\"submit\">Pause the agent</button>";
    private const string ResumeButton = "<button type=\"submit\">Resume the agent</button>";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    [ShowsAuditOf(AuditAction.AgentStarted, AuditAction.AgentPaused, AuditAction.AgentResumed, AuditAction.AgentStopped)]
    public async Task A_user_starts_pauses_resumes_and_stops_an_agent_and_every_change_is_recorded_and_shown()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"));
        await application.GiveTeamsAsync(user.Id, Morning);
        var agent = Assert.Single(StoredAgent.All(application.Database));
        var statePage = AgentBrowser.AgentStatePage(agent.Id);

        // The list leads to the page of the agent's state, and so does the page of its settings.
        Assert.Contains($"<a href=\"{statePage}\">State of Swing</a>", await client.PageAsync(AgentBrowser.AgentsPage));
        Assert.Contains($"<a href=\"{statePage}\">State of the agent</a>", await client.PageAsync(AgentBrowser.AgentPage(agent.Id)));

        // A new agent is a draft. The page says so, with what that means and why it is one, and
        // offers to start it, and nothing else: a draft is neither paused, resumed, nor stopped.
        var draft = AgentBrowser.Read(await client.PageAsync(statePage));

        Assert.Contains("<h1>State of the agent Swing</h1>", draft);
        Assert.Contains(
            $"<p>State: <strong>Draft</strong>, since 12 November 2026, 09:00 UTC. {AgentPages.Explain(AgentState.Draft)}</p>", draft);
        Assert.Contains("<p>You created the agent.</p>", draft);
        Assert.Contains(StartButton, draft);
        Assert.DoesNotContain(PauseButton, draft);
        Assert.DoesNotContain(ResumeButton, draft);
        Assert.DoesNotContain("Stop the agent", draft);
        Assert.DoesNotContain(AgentPages.ClosePositionNow, draft);
        Assert.Equal([("12 November 2026, 09:00 UTC", "Draft", "You created the agent.")], ChangesOn(draft));

        // Started, it runs. The answer is the page itself, by a request that changes nothing.
        clock.Set(Morning.AddMinutes(1));
        var (path, running) = await client.StartAgentAsync(agent.Id);

        Assert.Equal(statePage, path);
        Assert.Equal(("Running", "UserStarted", Morning.AddMinutes(1)), StateOf(application, agent.Id));
        Assert.Contains("<strong>Running</strong>, since 12 November 2026, 09:01 UTC.", running);
        Assert.Contains(AgentPages.Explain(AgentState.Running), AgentBrowser.Read(running));
        Assert.Contains("<p>You started the agent.</p>", running);
        Assert.Contains(PauseButton, running);
        Assert.DoesNotContain(StartButton, running);
        Assert.DoesNotContain(ResumeButton, running);
        Assert.Contains($"<a href=\"{AgentBrowser.StopAgentPage(agent.Id)}\">Stop the agent</a>", running);

        // The list says the state as well, and what each state means.
        var list = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentsPage));

        Assert.Contains("<td>Running</td>", list);
        Assert.All(Enum.GetValues<AgentState>(), state => Assert.Contains($"<dd>{AgentPages.Explain(state)}</dd>", list));

        // Paused, it runs no cycle, and is resumed.
        clock.Set(Morning.AddMinutes(2));
        var (_, paused) = await client.PauseAgentAsync(agent.Id);

        Assert.Equal(("Paused", "UserPaused", Morning.AddMinutes(2)), StateOf(application, agent.Id));
        Assert.Contains("<strong>Paused</strong>, since 12 November 2026, 09:02 UTC.", paused);
        Assert.Contains("<p>You paused the agent.</p>", paused);
        Assert.Contains(ResumeButton, paused);
        Assert.DoesNotContain(PauseButton, paused);
        Assert.DoesNotContain(StartButton, paused);

        clock.Set(Morning.AddMinutes(3));
        var (_, resumed) = await client.ResumeAgentAsync(agent.Id);

        Assert.Equal(("Running", "UserResumed", Morning.AddMinutes(3)), StateOf(application, agent.Id));
        Assert.Contains("<p>You resumed the agent.</p>", resumed);
        Assert.Contains(PauseButton, resumed);

        // Stopped, on the page that asks once more. It has no position, so nothing is chosen.
        clock.Set(Morning.AddMinutes(4));
        var stopPage = AgentBrowser.Read(await client.PageAsync(AgentBrowser.StopAgentPage(agent.Id)));

        Assert.Contains("<h1>Stop the agent Swing</h1>", stopPage);
        Assert.Contains("<p>The agent has no open position.</p>", stopPage);
        Assert.DoesNotContain("type=\"radio\"", stopPage);
        Assert.Contains($"<a href=\"{statePage}\">Keep the agent as it is</a>", stopPage);
        Assert.Equal("Running", StateOf(application, agent.Id).State);

        var (stoppedAt, stopped) = await client.StopAgentAsync(agent.Id);

        Assert.Equal(statePage, stoppedAt);
        Assert.Equal(("Stopped", "UserStopped", Morning.AddMinutes(4)), StateOf(application, agent.Id));
        Assert.Contains("<strong>Stopped</strong>, since 12 November 2026, 09:04 UTC.", stopped);
        Assert.Contains("<p>You stopped the agent. It had no open position.</p>", stopped);
        Assert.Contains(StartButton, stopped);
        Assert.DoesNotContain("Stop the agent", stopped);

        // Every change is recorded with its cause and its time, in order, and the page shows
        // them all, latest first (FR-AGT-10).
        Assert.Equal(
            [
                ("Draft", "Created", Morning),
                ("Running", "UserStarted", Morning.AddMinutes(1)),
                ("Paused", "UserPaused", Morning.AddMinutes(2)),
                ("Running", "UserResumed", Morning.AddMinutes(3)),
                ("Stopped", "UserStopped", Morning.AddMinutes(4)),
            ],
            StoredAgentStateChange.Of(application.Database, agent.Id).Select(change => (change.State, change.Cause, change.ChangedAt)));
        Assert.All(StoredAgentStateChange.All(application.Database), change => Assert.Equal(user.Id, change.UserId));

        Assert.Equal(
            [
                ("12 November 2026, 09:04 UTC", "Stopped", "You stopped the agent. It had no open position."),
                ("12 November 2026, 09:03 UTC", "Running", "You resumed the agent."),
                ("12 November 2026, 09:02 UTC", "Paused", "You paused the agent."),
                ("12 November 2026, 09:01 UTC", "Running", "You started the agent."),
                ("12 November 2026, 09:00 UTC", "Draft", "You created the agent."),
            ],
            ChangesOn(AgentBrowser.Read(stopped)));

        // The audit trail has what the user did to the state of the agent, by the id of the
        // agent, with the time and where the request came from (NFR-LOG-7). Creating it is none.
        var events = StoredAuditEvent.All(application.Database)
            .Where(recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [
                $"AgentStarted agent:{agent.Id} {Morning.AddMinutes(1):O}",
                $"AgentPaused agent:{agent.Id} {Morning.AddMinutes(2):O}",
                $"AgentResumed agent:{agent.Id} {Morning.AddMinutes(3):O}",
                $"AgentStopped agent:{agent.Id} {Morning.AddMinutes(4):O}",
            ],
            events.Select(recorded => $"{recorded.Action} {recorded.Target} {recorded.OccurredAt:O}"));
        Assert.All(events, recorded => Assert.Equal((user.Id, "203.0.113.7"), (recorded.UserId, recorded.Origin)));

        // The log tells the same, by ids, and never by the name of the agent.
        var moved = application.Events().Where(written => written.Template == MovedTemplate).ToList();

        Assert.Equal(
            ["Draft Running UserStarted", "Running Paused UserPaused", "Paused Running UserResumed"],
            moved.Select(written => $"{written.Text("PreviousAgentState")} {written.Text("AgentState")} {written.Text("AgentStateCause")}"));
        Assert.All(moved, written => Assert.Equal((user.Id.ToString(), agent.Id.ToString()), (written.Text("UserId"), written.Text("AgentId"))));
        Assert.Single(application.Events(), written => written.Template == StoppedTemplate);
        Assert.DoesNotContain(application.Events(), written => written.ToString().Contains("Swing", StringComparison.Ordinal));

        // The download of the user's data has the changes too, each with the agent it is of.
        var export = JsonDocument.Parse(await client.PageAsync("/account/export")).RootElement;
        var exported = export.GetProperty("agentStateChanges").EnumerateArray().ToList();

        Assert.Equal(
            ["Draft Created", "Running UserStarted", "Paused UserPaused", "Running UserResumed", "Stopped UserStopped"],
            exported.Select(change => $"{change.GetProperty("state").GetString()} {change.GetProperty("cause").GetString()}"));
        Assert.All(exported, change =>
        {
            Assert.Equal(["agentId", "mode", "state", "cause", "changedAt"], change.EnumerateObject().Select(property => property.Name));
            Assert.Equal("Demo", change.GetProperty("mode").GetString());
            Assert.Equal(agent.Id, change.GetProperty("agentId").GetGuid());
        });
        Assert.Equal(Morning.AddMinutes(4), exported[^1].GetProperty("changedAt").GetDateTimeOffset());

        // A stopped agent is started again, and runs.
        clock.Set(Morning.AddMinutes(5));
        await client.StartAgentAsync(agent.Id);

        Assert.Equal(("Running", "UserStarted", Morning.AddMinutes(5)), StateOf(application, agent.Id));
        Assert.Equal(6, StoredAgentStateChange.Of(application.Database, agent.Id).Count);
    }

    [Fact]
    public async Task A_form_that_is_sent_twice_is_answered_as_the_first_one_was_and_changes_nothing_more()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry());
        await application.GiveTeamsAsync(user.Id, Morning);
        var agent = Assert.Single(StoredAgent.All(application.Database));
        var statePage = AgentBrowser.AgentStatePage(agent.Id);
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(statePage));

        // Each form twice, the second time at a later moment: the agent is where the first one
        // left it, which is what was asked for, and nothing is recorded again.
        (string Page, string Form, string State)[] forms =
        [
            (statePage, AgentBrowser.StartForm, "Running"),
            (statePage, AgentBrowser.PauseForm, "Paused"),
            (statePage, AgentBrowser.ResumeForm, "Running"),
            (AgentBrowser.StopAgentPage(agent.Id), AgentBrowser.StopForm, "Stopped"),
        ];

        for (var step = 0; step < forms.Length; step++)
        {
            var (page, form, state) = forms[step];

            clock.Set(Morning.AddMinutes(10 * (step + 1)));
            Assert.Equal(statePage, (await client.PostAsync(page, form, token)).Path);

            clock.Set(Morning.AddMinutes((10 * (step + 1)) + 5));
            Assert.Equal(statePage, (await client.PostAsync(page, form, token)).Path);

            Assert.Equal((state, Morning.AddMinutes(10 * (step + 1))), (StateOf(application, agent.Id).State, StateOf(application, agent.Id).ChangedAt));
        }

        Assert.Equal(
            ["Draft: Created", "Running: UserStarted", "Paused: UserPaused", "Running: UserResumed", "Stopped: UserStopped"],
            StoredAgentStateChange.Told(application.Database, agent.Id));
        Assert.Equal(
            ["AgentStarted", "AgentPaused", "AgentResumed", "AgentStopped"],
            StoredAuditEvent.All(application.Database).Select(recorded => recorded.Action).Where(action => action.StartsWith("Agent", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(AgentState.Draft, AgentBrowser.PauseForm, "This agent is a draft. Only a running agent is paused.")]
    [InlineData(AgentState.Draft, AgentBrowser.ResumeForm, "This agent is a draft. An agent is resumed when it is paused, is halted, or requires attention.")]
    [InlineData(AgentState.Paused, AgentBrowser.StartForm, "This agent is paused. Only a draft or a stopped agent is started.")]
    [InlineData(AgentState.Halted, AgentBrowser.PauseForm, "This agent is halted. Only a running agent is paused.")]
    [InlineData(AgentState.AttentionRequired, AgentBrowser.StartForm, "This agent is waiting for your attention. Only a draft or a stopped agent is started.")]
    [InlineData(AgentState.Stopped, AgentBrowser.PauseForm, "This agent is stopped. Only a running agent is paused.")]
    [InlineData(AgentState.Stopped, AgentBrowser.ResumeForm, "This agent is stopped. An agent is resumed when it is paused, is halted, or requires attention.")]
    public async Task A_control_that_is_not_offered_in_the_state_of_the_agent_changes_nothing(AgentState state, string form, string says)
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning.AddHours(1)));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, state: state);
        var stored = application.RowsOf(user.Id);

        // The page has no such form in this state. It is posted all the same, as something that
        // is not a page of the application can: the control is decided on the server.
        var statePage = AgentBrowser.AgentStatePage(agent);
        var page = await client.PageAsync(statePage);
        Assert.DoesNotContain($"name=\"_handler\" value=\"{form}\"", page);

        var (path, answer) = await client.PostAsync(statePage, form, Browser.AntiforgeryTokenIn(page));

        Assert.Equal(statePage, path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{says}</p>", answer);

        // The answer shows the agent as it is, with the controls of its state and no other.
        Assert.Equal(
            AgentStates.Offered(state).Where(control => control != AgentControl.Stop).Select(AgentPages.Describe),
            Regex.Matches(answer, "<button type=\"submit\">((?:Start|Pause|Resume) the agent)</button>").Select(button => button.Groups[1].Value));

        Assert.Equal(stored["agents"], application.RowsOf(user.Id)["agents"]);
        Assert.Equal(stored["agent_state_changes"], application.RowsOf(user.Id)["agent_state_changes"]);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AgentState.Halted)]
    [InlineData(AgentState.AttentionRequired)]
    [InlineData(AgentState.Paused)]
    public async Task An_agent_that_does_not_run_is_resumed_or_stopped_by_its_user(AgentState state)
    {
        var clock = new TestClock(Morning.AddHours(1));
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);

        // A loss limit halts an agent, and a key that goes makes one require attention: the
        // test stores the agents in the state, as it was left when they were created. One is
        // resumed, and the other stopped. An agent that its daily loss limit halted is resumed
        // from the next UTC day: see AgentHaltTests.
        var resumed = await application.GiveAgentAsync(user.Id, Morning, name: "Resumed", state: state);
        var stopped = await application.GiveAgentAsync(user.Id, Morning, name: "Stopped", state: state, symbol: "SOLUSDT");

        // The page says the state and what it means, and offers to resume the agent or to stop it.
        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(resumed)));

        Assert.Contains($"<strong>{AgentPages.Describe(state)}</strong>, since 12 November 2026, 09:00 UTC. {AgentPages.Explain(state)}", page);
        Assert.Contains(ResumeButton, page);
        Assert.DoesNotContain(StartButton, page);
        Assert.DoesNotContain(PauseButton, page);
        Assert.Contains($"<a href=\"{AgentBrowser.StopAgentPage(resumed)}\">Stop the agent</a>", page);

        Assert.Equal(AgentBrowser.AgentStatePage(resumed), (await client.ResumeAgentAsync(resumed)).Path);
        Assert.Equal(("Running", "UserResumed", Morning.AddHours(1)), StateOf(application, resumed));

        Assert.Equal(AgentBrowser.AgentStatePage(stopped), (await client.StopAgentAsync(stopped)).Path);
        Assert.Equal(("Stopped", "UserStopped", Morning.AddHours(1)), StateOf(application, stopped));

        Assert.Equal(
            ["AgentResumed", "AgentStopped"],
            StoredAuditEvent.All(application.Database).Select(recorded => recorded.Action).Where(action => action.StartsWith("Agent", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_key_that_is_missing_or_lacks_the_permission_of_the_market_blocks_the_start_with_a_message_that_names_it()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "On Spot"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "On Futures", market: "Futures"));
        await application.GiveTeamsAsync(user.Id, Morning);
        var spot = StoredAgent.Named(application.Database, "On Spot");
        var futures = StoredAgent.Named(application.Database, "On Futures");

        // Ada has stored no Binance key. The agent does not start, and the page says why, and
        // leads to her keys. Nothing is recorded: the agent is the draft it was.
        var (path, noKey) = await client.StartAgentAsync(spot.Id);

        Assert.Equal(AgentBrowser.AgentStatePage(spot.Id), path);
        Assert.Contains(
            $"<p class=\"form-error\" role=\"alert\">{KeyPages.Says(StartBlock.NoKey, TradingMode.Demo)}</p>", noKey);
        Assert.Contains("<a href=\"/account/keys\">Your keys</a>", noKey);
        Assert.Contains(StartButton, noKey);
        Assert.Equal(("Draft", "Created", Morning), StateOf(application, spot.Id));

        // She stores a key for Demo that may trade on Futures, and not on Spot.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInDemo with { SpotTrading = false };
        await client.ConfirmIdentityAsync();
        await client.StoreExchangeKeyAsync("demo", Keys.BinanceApi, Keys.BinanceSecret);

        // The agent on Spot is blocked, with the message that names the permission (FR-KEY-5).
        clock.Set(Morning.AddMinutes(5));
        var (_, lacks) = await client.StartAgentAsync(spot.Id);

        Assert.Contains(
            "Your Binance key for Demo lacks the Spot trading permission, which this agent's market needs.", AgentBrowser.Read(lacks));
        Assert.DoesNotContain("Futures trading permission", lacks);
        Assert.Equal("Draft", StateOf(application, spot.Id).State);

        // The one on Futures starts with the same key.
        Assert.Equal(AgentBrowser.AgentStatePage(futures.Id), (await client.StartAgentAsync(futures.Id)).Path);
        Assert.Equal(("Running", "UserStarted", Morning.AddMinutes(5)), StateOf(application, futures.Id));

        // What blocks a start blocks a resume as well: a paused agent on Spot stays paused.
        var paused = await application.GiveAgentAsync(user.Id, Morning, name: "Paused", state: AgentState.Paused, symbol: "SOLUSDT");
        var (_, notResumed) = await client.ResumeAgentAsync(paused);

        Assert.Contains("lacks the Spot trading permission", AgentBrowser.Read(notResumed));
        Assert.Equal("Paused", StateOf(application, paused).State);

        // Pausing asks nothing of the key: the agent on Futures is paused, whatever the key may do.
        await client.PauseAgentAsync(futures.Id);

        Assert.Equal("Paused", StateOf(application, futures.Id).State);

        // She turns Spot trading on at Binance and has the key checked again. The same form
        // then starts the agent.
        application.Providers.BinanceAnswer = (_, _) => StandInProviders.TakenInDemo;
        clock.Set(Morning.AddMinutes(10));
        await client.CheckExchangeKeyAsync("demo");

        Assert.Equal(AgentBrowser.AgentStatePage(spot.Id), (await client.StartAgentAsync(spot.Id)).Path);
        Assert.Equal(("Running", "UserStarted", Morning.AddMinutes(10)), StateOf(application, spot.Id));

        // What was refused is in no trail, and the log names the block and never the key.
        Assert.Equal(
            ["AgentStarted", "AgentPaused", "AgentStarted"],
            StoredAuditEvent.All(application.Database).Select(recorded => recorded.Action).Where(action => action.StartsWith("Agent", StringComparison.Ordinal)));

        var refused = application.Events()
            .Where(written => written.Template.Contains("the Binance key for {TradingMode} blocks it", StringComparison.Ordinal))
            .Select(written => $"{written.Text("AgentControl")} {written.Text("StartBlock")}")
            .ToList();

        Assert.Equal(["Start NoKey", "Start SpotTradingMissing", "Resume SpotTradingMissing"], refused);
    }

    [Fact]
    public async Task A_second_agent_for_the_same_key_market_and_symbol_starts_only_once_the_first_is_stopped()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "First"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Second", timeframe: "4H"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "On Futures", market: "Futures"));
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Another symbol", symbol: "SOLUSDT"));
        await application.GiveTeamsAsync(user.Id, Morning);
        var first = StoredAgent.Named(application.Database, "First");
        var second = StoredAgent.Named(application.Database, "Second");

        // The first runs, and is paused: it is active all the same.
        await client.StartAgentAsync(first.Id);
        await client.PauseAgentAsync(first.Id);

        // The second trades the same symbol on the same market with the same key. It does not
        // start, and the page says which agent stands in the way of it (FR-AGT-9).
        var (path, refused) = await client.StartAgentAsync(second.Id);

        Assert.Equal(AgentBrowser.AgentStatePage(second.Id), path);
        Assert.Contains(
            "<p class=\"form-error\" role=\"alert\">You have another active agent for BTCUSDT on Spot in Demo. One Binance key runs " +
            "one active agent for each market and symbol. Stop the other one first.</p>",
            refused);
        Assert.Equal(("Draft", "Created", Morning), StateOf(application, second.Id));
        Assert.Equal(["Draft: Created"], StoredAgentStateChange.Told(application.Database, second.Id));

        // An agent on the other market starts, and so does one for another symbol.
        await client.StartAgentAsync(StoredAgent.Named(application.Database, "On Futures").Id);
        await client.StartAgentAsync(StoredAgent.Named(application.Database, "Another symbol").Id);

        Assert.Equal(
            ["Another symbol: Running", "First: Paused", "On Futures: Running", "Second: Draft"],
            StoredAgent.All(application.Database).Select(agent => $"{agent.Name}: {agent.State}").Order());

        // Once the first is stopped, the second starts. And then the first does not.
        await client.StopAgentAsync(first.Id);
        Assert.Equal(AgentBrowser.AgentStatePage(second.Id), (await client.StartAgentAsync(second.Id)).Path);
        Assert.Equal("Running", StateOf(application, second.Id).State);

        var (_, firstRefused) = await client.StartAgentAsync(first.Id);

        Assert.Contains("You have another active agent for BTCUSDT on Spot in Demo.", firstRefused);
        Assert.Equal("Stopped", StateOf(application, first.Id).State);
    }

    [Fact]
    public async Task A_draft_is_not_stopped()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var draft = await application.GiveAgentAsync(user.Id, Morning);
        var stored = application.RowsOf(user.Id);

        // The page that stops an agent has no form for a draft: it was never started.
        var page = await client.PageAsync(AgentBrowser.StopAgentPage(draft));

        Assert.Contains("<p role=\"status\">This agent is a draft: it was never started, so there is nothing to stop.</p>", page);
        Assert.DoesNotContain("<button type=\"submit\">Stop the agent</button>", page);

        // A form that is posted all the same stops nothing.
        var (path, answer) = await client.PostAsync(AgentBrowser.StopAgentPage(draft), AgentBrowser.StopForm, Browser.AntiforgeryTokenIn(page));

        Assert.Equal(AgentBrowser.StopAgentPage(draft), path);
        Assert.Contains("This agent is a draft: it was never started, so there is nothing to stop.", answer);
        Assert.Equal(stored["agents"], application.RowsOf(user.Id)["agents"]);
        Assert.Equal(stored["agent_state_changes"], application.RowsOf(user.Id)["agent_state_changes"]);

        // And a stopped agent is said to be stopped, with no button to stop it again.
        var stopped = await application.GiveAgentAsync(user.Id, Morning, name: "Stopped", state: AgentState.Stopped, symbol: "SOLUSDT");
        var again = await client.PageAsync(AgentBrowser.StopAgentPage(stopped));

        Assert.Contains("<p role=\"status\">This agent is stopped.</p>", again);
        Assert.DoesNotContain("<button type=\"submit\">Stop the agent</button>", again);
    }

    [Fact]
    public async Task The_page_shows_the_latest_changes_of_an_agent_with_many()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        // An agent that was paused and resumed thirty times has more changes than a page shows.
        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            for (var minute = 1; minute <= 60; minute++)
            {
                context.Add(new AgentStateChange(
                    Guid.CreateVersion7(Morning.AddMinutes(minute)),
                    agent,
                    user.Id,
                    TradingMode.Demo,
                    minute % 2 == 1 ? AgentState.Running : AgentState.Paused,
                    minute % 2 == 1 ? AgentStateCause.UserResumed : AgentStateCause.UserPaused,
                    Morning.AddMinutes(minute)));
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var page = await client.PageAsync(AgentBrowser.AgentStatePage(agent));
        var shown = ChangesOn(page);

        // The latest fifty, latest first, and the page says that there are more.
        Assert.Contains($"<p>The latest {AgentStateHistory.MostShown} of 61 changes, latest first.</p>", page);
        Assert.Equal(AgentStateHistory.MostShown, shown.Count);
        Assert.Equal(("12 November 2026, 10:00 UTC", "Paused", "You paused the agent."), shown[0]);
        Assert.Equal(("12 November 2026, 09:11 UTC", "Running", "You resumed the agent."), shown[^1]);
    }

    [Fact]
    [ProvesOwnershipOf("agent_state_changes")]
    public async Task The_state_of_an_agent_and_its_changes_are_out_of_reach_of_another_user()
    {
        var clock = new TestClock(Morning);
        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), clock, exposure);

        // Grace has an agent that runs, with an open position, and a draft.
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveKeysAsync(grace.Id, Morning);
        using var gracesBrowser = application.CreateClient();
        await gracesBrowser.SignInAsync(Grace);
        await gracesBrowser.CreateAgentAsync(AgentBrowser.Entry(name: "Grace's swing"));
        await gracesBrowser.CreateAgentAsync(AgentBrowser.Entry(name: "Grace's draft", symbol: "SOLUSDT"));
        await application.GiveTeamsAsync(grace.Id, Morning);
        var running = StoredAgent.Named(application.Database, "Grace's swing");
        var draft = StoredAgent.Named(application.Database, "Grace's draft");
        await gracesBrowser.StartAgentAsync(running.Id);
        exposure.Holds(running.Id);

        var before = application.RowsOf(grace.Id);
        Assert.Equal(3, before["agent_state_changes"].Count);

        // Ada has a key of her own, and an agent of her own.
        clock.Set(Morning.AddMinutes(5));
        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(ada.Id, clock.GetUtcNow());
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Ada's swing"));
        await application.GiveTeamsAsync(ada.Id, Morning);
        var own = Assert.Single(StoredAgent.Of(application.Database, ada.Id));
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(AgentBrowser.AgentStatePage(own.Id)));

        // The pages of an agent of Grace are not found for her, as those of an agent that does
        // not exist are not: nothing tells her that the id is someone's, or what state it is in.
        (string Page, string Form)[] forms =
        [
            (AgentBrowser.AgentStatePage(running.Id), AgentBrowser.PauseForm),
            (AgentBrowser.AgentStatePage(draft.Id), AgentBrowser.StartForm),
            (AgentBrowser.AgentStatePage(running.Id), AgentBrowser.ResumeForm),
            (AgentBrowser.StopAgentPage(running.Id), AgentBrowser.StopForm),
            (AgentBrowser.ClosePositionPage(running.Id), AgentBrowser.ClosePositionForm),
            (AgentBrowser.TestCyclePage(running.Id), AgentBrowser.TestCycleForm),
        ];

        foreach (var (page, form) in forms)
        {
            using var answer = await client.GetAsync(page, TestContext.Current.CancellationToken);
            var shown = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
            Assert.DoesNotContain("Grace", AgentBrowser.Read(shown));
            Assert.DoesNotContain("Running", shown);

            // A form that is posted at them all the same is not found either, and changes nothing.
            using var posted = await client.SendFormAsync(page, form, token, ("Position", "close"));

            Assert.Contains(posted.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.BadRequest });
        }

        // Nothing of Grace's was started, stopped, or closed, and nothing was asked of the exchange.
        Assert.Empty(exposure.Closes);
        Assert.Equal(TextOf(before), TextOf(application.RowsOf(grace.Id)));

        // What Ada starts and stops is her own: her agent runs for the same symbol as Grace's,
        // each with the key of its own user (FR-AGT-9).
        await client.StartAgentAsync(own.Id);
        await client.StopAgentAsync(own.Id);

        Assert.Equal(
            ["Draft: Created", "Running: UserStarted", "Stopped: UserStopped"],
            StoredAgentStateChange.Told(application.Database, own.Id));
        Assert.Equal(TextOf(before), TextOf(application.RowsOf(grace.Id)));

        // Her pages and her download show the changes of her agent, and none of Grace's.
        var export = await client.PageAsync("/account/export");

        Assert.Equal(3, JsonDocument.Parse(export).RootElement.GetProperty("agentStateChanges").GetArrayLength());
        Assert.DoesNotContain(running.Id.ToString(), export);
        Assert.DoesNotContain(draft.Id.ToString(), export);
        Assert.DoesNotContain(grace.Id.ToString(), export);

        static string TextOf(IReadOnlyDictionary<string, IReadOnlyList<string>> rows) =>
            string.Join(
                Environment.NewLine,
                rows.OrderBy(table => table.Key, StringComparer.Ordinal).SelectMany(table => table.Value.Select(row => $"{table.Key}: {row}")));
    }

    /// <summary>What the row of an agent says of its state: the state, its cause, and since when.</summary>
    internal static (string State, string Cause, DateTimeOffset ChangedAt) StateOf(AccountsApplication application, Guid agentId)
    {
        var agent = StoredAgent.All(application.Database).Single(stored => stored.Id == agentId);

        // The row of an agent and the latest of its changes say the same (FR-AGT-10).
        var latest = StoredAgentStateChange.Of(application.Database, agentId)[^1];
        Assert.Equal((agent.State, agent.StateCause, agent.StateChangedAt), (latest.State, latest.Cause, latest.ChangedAt));

        return (agent.State, agent.StateCause, agent.StateChangedAt);
    }

    /// <summary>The changes of a state that a page lists, in the order of the page: when, the state, and why.</summary>
    internal static IReadOnlyList<(string When, string State, string Why)> ChangesOn(string page) =>
        [.. RowsOfChangesOn(page).Select(row => (row.Groups[1].Value, row.Groups[3].Value, row.Groups[4].Value))];

    /// <summary>The mode that a page says for each change of a state, in the order of the page: the word of its badge.</summary>
    internal static IReadOnlyList<string> ModesOfChangesOn(string page) =>
        [.. RowsOfChangesOn(page).Select(row => Regex.Replace(row.Groups[2].Value, "<[^>]+>", string.Empty).Trim())];

    /// <summary>The rows of the table of changes on a page: when, the mode, the state, and why.</summary>
    private static IReadOnlyList<Match> RowsOfChangesOn(string page)
    {
        var table = Regex.Match(page, "<caption>State changes</caption>.*?</table>", RegexOptions.Singleline);
        Assert.True(table.Success, $"The page lists no changes of a state:{Environment.NewLine}{page}");

        return Regex
            .Matches(
                table.Value,
                @"<tr>\s*<td>(.*?)</td>\s*<td>(.*?)</td>\s*<td>(.*?)</td>\s*<td>(.*?)</td>\s*</tr>",
                RegexOptions.Singleline)
            .ToList();
    }
}
