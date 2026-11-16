using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// Every action that affects Live trading asks for confirmation (FR-UI-3). Stopping an agent
/// and closing its position ask once more in either mode. For an agent in Live, starting,
/// pausing, and resuming it ask once more as well, and so does a change to its capital or to a
/// risk setting. What asks is decided on the server, of the mode that the agent is in then: a
/// form of a page that did not ask changes nothing of an agent in Live.
/// </summary>
public class AgentLiveConfirmationTests
{
    private const string Ada = "ada@example.com";

    private const string StartButton = "<button type=\"submit\">Start the agent</button>";
    private const string PauseButton = "<button type=\"submit\">Pause the agent</button>";
    private const string ResumeButton = "<button type=\"submit\">Resume the agent</button>";
    private const string SaveForLiveButton = "<button type=\"submit\" name=\"Confirm\" value=\"live\">Save for Live</button>";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Starting_pausing_and_resuming_an_agent_in_Live_each_ask_once_more()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", mode: TradingMode.Live);
        var statePage = AgentBrowser.AgentStatePage(agent);

        (string Control, string Says, string State, string Action)[] controls =
        [
            ("start", "Start the agent", "Running", "AgentStarted"),
            ("pause", "Pause the agent", "Paused", "AgentPaused"),
            ("resume", "Resume the agent", "Running", "AgentResumed"),
        ];

        var minute = 0;

        foreach (var (control, says, state, action) in controls)
        {
            var button = $"<button type=\"submit\">{says}</button>";
            var before = AgentControlTests.StateOf(application, agent).State;
            var confirmPage = AgentBrowser.ConfirmControlPage(agent, control);

            // The page of the state has no button that acts with one press. It leads to the
            // page that asks once more, as it does for stopping an agent.
            var shown = await client.PageAsync(statePage);

            Assert.DoesNotContain(button, shown);
            Assert.Contains($"<a href=\"{confirmPage}\">{says}</a>", shown);

            // That page says what the control does, and that it acts on the real account.
            // Opening it changes nothing.
            var asks = AgentBrowser.Read(await client.PageAsync(confirmPage));

            Assert.Contains($"<h1>{says} Swing</h1>", asks);
            Assert.Contains($"<p>Mode: {AgentBrowser.Badge(TradingMode.Live)} The agent trades in your real Binance account.</p>", asks);
            Assert.Contains($"<p><strong>{AgentPages.ActsOnLive}</strong></p>", asks);
            Assert.Contains(button, asks);
            Assert.Contains($"<a href=\"{statePage}\">Keep the agent as it is</a>", asks);
            Assert.Equal(before, AgentControlTests.StateOf(application, agent).State);

            // Its button does what was asked, and leads to the page of the state.
            clock.Set(Morning.AddMinutes(++minute));
            var (path, _) = await client.ConfirmControlAsync(agent, control);

            Assert.Equal(statePage, path);
            Assert.Equal(state, AgentControlTests.StateOf(application, agent).State);
            Assert.Equal(action, StoredAuditEvent.All(application.Database).Last(recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal)).Action);
        }

        // Each was recorded once, and each change of the state says that it happened in Live.
        Assert.Equal(
            ["AgentStarted", "AgentPaused", "AgentResumed"],
            StoredAuditEvent.All(application.Database)
                .Where(recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal))
                .Select(recorded => recorded.Action));
        Assert.Equal(
            ["Live Draft", "Live Running", "Live Paused", "Live Running"],
            StoredAgentStateChange.Of(application.Database, agent).Select(change => $"{change.Mode} {change.State}"));

        // Stopping it asks once more as it always did, and says that it acts on the real account.
        var stop = AgentBrowser.Read(await client.PageAsync(AgentBrowser.StopAgentPage(agent)));

        Assert.Contains($"<p><strong>{AgentPages.ActsOnLive}</strong></p>", stop);
        Assert.Contains(AgentBrowser.Badge(TradingMode.Live), stop);
    }

    [Fact]
    public async Task A_one_press_form_posted_for_an_agent_in_Live_changes_nothing_and_leads_to_the_page_that_asks()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();

        // An agent in Demo, whose page of the state has the button that starts it with one press.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"));
        await application.GiveTeamsAsync(user.Id, Morning);
        var agent = Assert.Single(StoredAgent.All(application.Database)).Id;
        var statePage = AgentBrowser.AgentStatePage(agent);
        var stale = await client.PageAsync(statePage);

        Assert.Contains(StartButton, stale);

        // It is switched to Live, on another browser. The form of the page that showed it in
        // Demo starts nothing: what asks is decided of the mode that the agent is in now.
        await client.SwitchToLiveAsync(agent);
        clock.Set(Morning.AddMinutes(1));
        var (path, asks) = await client.PostAsync(statePage, AgentBrowser.StartForm, Browser.AntiforgeryTokenIn(stale));

        Assert.Equal(AgentBrowser.ConfirmControlPage(agent, "start"), path);
        Assert.Contains("<h1>Start the agent Swing</h1>", asks);
        Assert.Equal(("Draft", "Created", Morning), AgentControlTests.StateOf(application, agent));
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentStarted");

        // So it is for an agent that runs in Live: neither pausing nor resuming is one press.
        var running = await application.GiveAgentAsync(
            user.Id, Morning, name: "Running", state: AgentState.Running, symbol: "SOLUSDT", mode: TradingMode.Live);
        var paused = await application.GiveAgentAsync(
            user.Id, Morning, name: "Paused", state: AgentState.Paused, market: Market.Futures, mode: TradingMode.Live);
        var token = Browser.AntiforgeryTokenIn(stale);

        Assert.Equal(
            AgentBrowser.ConfirmControlPage(running, "pause"),
            (await client.PostAsync(AgentBrowser.AgentStatePage(running), AgentBrowser.PauseForm, token)).Path);
        Assert.Equal(
            AgentBrowser.ConfirmControlPage(paused, "resume"),
            (await client.PostAsync(AgentBrowser.AgentStatePage(paused), AgentBrowser.ResumeForm, token)).Path);

        Assert.Equal("Running", AgentControlTests.StateOf(application, running).State);
        Assert.Equal("Paused", AgentControlTests.StateOf(application, paused).State);
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database),
            recorded => recorded.Action is "AgentStarted" or "AgentPaused" or "AgentResumed");

        // A form that is sent twice is answered as the first one was, also in Live: the agent
        // is where the control leads, and nothing is asked about it.
        await client.ConfirmControlAsync(agent, "start");

        Assert.Equal(statePage, (await client.PostAsync(statePage, AgentBrowser.StartForm, token)).Path);
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentStarted");

        // A path that names no control of that page is no page.
        using var unknown = await client.GetAsync($"/agents/{agent}/confirm/stop", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task An_agent_in_Demo_is_started_paused_and_resumed_with_one_press()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing");
        var statePage = AgentBrowser.AgentStatePage(agent);

        // No money is at stake in Demo: the page of the state has the buttons, and leads to no
        // page that asks once more.
        var draft = await client.PageAsync(statePage);

        Assert.Contains(StartButton, draft);
        Assert.DoesNotContain("/confirm/", draft);

        Assert.Equal(statePage, (await client.StartAgentAsync(agent)).Path);
        Assert.Contains(PauseButton, await client.PageAsync(statePage));
        Assert.Equal(statePage, (await client.PauseAgentAsync(agent)).Path);
        Assert.Contains(ResumeButton, await client.PageAsync(statePage));
        Assert.Equal(statePage, (await client.ResumeAgentAsync(agent)).Path);

        Assert.Equal("Running", AgentControlTests.StateOf(application, agent).State);

        // The page that asks once more is there for it all the same, and does not speak of
        // real money.
        var asks = AgentBrowser.Read(await client.PageAsync(AgentBrowser.ConfirmControlPage(agent, "pause")));

        Assert.Contains("<h1>Pause the agent Swing</h1>", asks);
        Assert.Contains(AgentBrowser.Badge(TradingMode.Demo), asks);
        Assert.DoesNotContain(AgentPages.ActsOnLive, asks);
    }

    [Fact]
    public async Task Changing_the_capital_or_a_risk_setting_of_an_agent_in_Live_asks_once_more_and_renaming_it_does_not()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // An agent that runs in Live, with the settings that the form of the tests brings.
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", state: AgentState.Running, mode: TradingMode.Live);
        var settingsPage = AgentBrowser.AgentPage(agent);
        var stored = StoredAgent.Named(application.Database, "Swing");

        // A new name is no change to what the agent does with money: it is saved with one press.
        Assert.Equal(AgentBrowser.AgentsPage, (await client.SaveAgentAsync(agent, AgentBrowser.Entry(name: "Renamed"))).Path);
        Assert.Equal(stored with { Name = "Renamed" }, StoredAgent.All(application.Database).Single());

        // A risk setting is. Nothing is stored: the form is back as it was entered, says that
        // the agent is in Live, and has a button that says so once more.
        clock.Set(Morning.AddMinutes(1));
        var changed = AgentBrowser.Entry(name: "Renamed", riskPerTrade: "3");
        var (path, asks) = await client.SaveAgentAsync(agent, changed);

        Assert.Equal(settingsPage, path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.ConfirmSaveForLive}</p>", AgentBrowser.Read(asks));
        Assert.Contains(SaveForLiveButton, asks);
        Assert.DoesNotContain("<button type=\"submit\">Save the agent</button>", asks);
        Assert.Equal("3", ValueOf(asks, "RiskPerTradePercent"));
        Assert.Equal(1.5m, StoredAgent.All(application.Database).Single().RiskPerTradePercent);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentRiskSettingsChanged");

        // With that button, it is stored, and recorded as a change to a risk setting is.
        var token = Browser.AntiforgeryTokenIn(asks);
        var (saved, _) = await client.PostAsync(settingsPage, AgentBrowser.SaveForm, token, [.. changed, ("Confirm", "live")]);

        Assert.Equal(AgentBrowser.AgentsPage, saved);
        Assert.Equal(3m, StoredAgent.All(application.Database).Single().RiskPerTradePercent);
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentRiskSettingsChanged");

        // The capital is asked about in the same way, and what else a form may bring does not
        // stand for the button.
        var more = AgentBrowser.Entry(name: "Renamed", riskPerTrade: "3", capital: "2500");

        Assert.Equal(settingsPage, (await client.SaveAgentAsync(agent, more)).Path);
        Assert.Equal(settingsPage, (await client.PostAsync(settingsPage, AgentBrowser.SaveForm, token, [.. more, ("Confirm", "yes")])).Path);
        Assert.Equal(1000m, StoredAgent.All(application.Database).Single().AllocatedCapital);

        await client.PostAsync(settingsPage, AgentBrowser.SaveForm, token, [.. more, ("Confirm", "live")]);

        Assert.Equal(2500m, StoredAgent.All(application.Database).Single().AllocatedCapital);

        // The settings as they are, however the numbers are written, change nothing, and are
        // not asked about.
        Assert.Equal(
            AgentBrowser.AgentsPage,
            (await client.SaveAgentAsync(agent, AgentBrowser.Entry(name: "Renamed", riskPerTrade: "3.00", capital: "2500.0"))).Path);

        // An agent in Demo is not asked about: no money is at stake there.
        var inDemo = await application.GiveAgentAsync(user.Id, Morning, name: "In Demo", symbol: "SOLUSDT");

        Assert.Equal(
            AgentBrowser.AgentsPage,
            (await client.SaveAgentAsync(inDemo, AgentBrowser.Entry(name: "In Demo", symbol: "SOLUSDT", riskPerTrade: "3", capital: "2500"))).Path);
        Assert.Equal(3m, StoredAgent.Named(application.Database, "In Demo").RiskPerTradePercent);
    }

    /// <summary>What the field of a name holds on a page.</summary>
    private static string? ValueOf(string page, string field)
    {
        var input = Regex.Match(page, $"<input[^>]* name=\"{Regex.Escape(field)}\"[^>]*>");
        Assert.True(input.Success, $"The page has no field {field}:{Environment.NewLine}{page}");

        var value = Regex.Match(input.Value, " value=\"([^\"]*)\"");

        return value.Success ? value.Groups[1].Value : null;
    }
}
