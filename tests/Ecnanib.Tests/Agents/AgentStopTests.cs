using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// When a user stops an agent that has an open position, they choose to close the position at
/// market or to keep it with its protective orders (FR-AGT-11), and what was chosen is done.
/// Agents places no order: it asks whatever keeps positions, which a stand-in is here, because
/// positions are not built yet. The agent is stopped first, and its position is closed after.
/// </summary>
public class AgentStopTests
{
    private const string Ada = "ada@example.com";

    private const string StopButton = "<button type=\"submit\">Stop the agent</button>";
    private const string ChooseFirst = "<p class=\"form-error\" role=\"alert\">This agent has an open position. Choose what happens to it.</p>";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stopping_an_agent_that_has_an_open_position_asks_what_happens_to_it_and_stops_nothing_without_the_choice()
    {
        var (application, client, exposure, _, agent) = await RunningWithAPositionAsync();
        using var disposing = application;
        using var browser = client;

        // The page names both choices, each with what it means, and has made neither.
        var page = AgentBrowser.Read(await client.PageAsync(AgentBrowser.StopAgentPage(agent)));

        Assert.Contains("<legend>The agent has an open position. What happens to it?</legend>", page);
        Assert.Contains("<label for=\"stop-close\">Close the position at market</label>", page);
        Assert.Contains("<label for=\"stop-keep\">Keep the position with its protective orders</label>", page);
        Assert.Contains("The position is closed now, at the market price", page);
        Assert.Contains("The position stays open at Binance with its stop-loss and take-profit orders", page);
        Assert.Equal(2, CountOf(page, "type=\"radio\" name=\"Position\""));
        Assert.DoesNotContain("checked", page);
        Assert.Contains(StopButton, page);

        // A form that brings no choice stops nothing: the choice is the user's, and is not made
        // for them. The form comes back, and says so.
        var (path, answer) = await client.PostAsync(
            AgentBrowser.StopAgentPage(agent), AgentBrowser.StopForm, Browser.AntiforgeryTokenIn(page));

        Assert.Equal(AgentBrowser.StopAgentPage(agent), path);
        Assert.Contains(ChooseFirst, answer);
        Assert.Contains("type=\"radio\" name=\"Position\"", answer);
        Assert.Contains(StopButton, answer);

        // Nor does one that brings something else in place of a choice.
        var (_, again) = await client.StopAgentAsync(agent, "liquidate");

        Assert.Contains(ChooseFirst, again);
        Assert.Equal("Running", AgentControlTests.StateOf(application, agent).State);
        Assert.Empty(exposure.Closes);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action is "AgentStopped" or "AgentPositionCloseRequested");
    }

    [Fact]
    public async Task An_agent_is_stopped_and_its_position_closed_at_market_when_its_user_chooses_that()
    {
        var (application, client, exposure, user, agent) = await RunningWithAPositionAsync(host: host => host.From("203.0.113.7"));
        using var disposing = application;
        using var browser = client;

        // The position is closed after the agent is stopped: when the close is asked for, the
        // stop is stored. An agent whose position could not be closed is a stopped one.
        var stateAtClose = new List<string>();
        exposure.OnClose = closed => stateAtClose.Add(StoredAgent.All(application.Database).Single(stored => stored.Id == closed).State);

        var (path, page) = await client.StopAgentAsync(agent, AgentPages.ClosePosition);

        // The answer is the page of the agent's state, which says what was chosen.
        Assert.Equal(AgentBrowser.AgentStatePage(agent), path);
        Assert.Equal(("Stopped", "UserStoppedClosingPosition", Morning.AddMinutes(5)), AgentControlTests.StateOf(application, agent));
        Assert.Contains("<p>You stopped the agent, and chose to close its position at market.</p>", page);

        // It was asked for once, of what keeps positions, for this agent.
        Assert.Equal([agent], exposure.Closes);
        Assert.Equal(["Stopped"], stateAtClose);

        // The position is gone, and with it the control that closes one.
        Assert.DoesNotContain(AgentPages.ClosePositionNow, page);

        // The stop is recorded, and the close with it: it is a manual close (NFR-LOG-7).
        var events = StoredAuditEvent.All(application.Database)
            .Where(recorded => recorded.Action is "AgentStopped" or "AgentPositionCloseRequested")
            .ToList();

        Assert.Equal(
            [$"AgentStopped agent:{agent} {Morning.AddMinutes(5):O}", $"AgentPositionCloseRequested agent:{agent} {Morning.AddMinutes(5):O}"],
            events.Select(recorded => $"{recorded.Action} {recorded.Target} {recorded.OccurredAt:O}"));
        Assert.All(events, recorded => Assert.Equal((user, "203.0.113.7"), (recorded.UserId, recorded.Origin)));

        // A form that is sent twice finds the agent stopped, and asks for nothing to be closed again.
        var token = Browser.AntiforgeryTokenIn(page);
        Assert.Equal(
            AgentBrowser.AgentStatePage(agent),
            (await client.PostAsync(AgentBrowser.StopAgentPage(agent), AgentBrowser.StopForm, token, ("Position", AgentPages.ClosePosition))).Path);

        Assert.Equal([agent], exposure.Closes);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task An_agent_is_stopped_and_its_position_kept_with_its_protective_orders_when_its_user_chooses_that()
    {
        var (application, client, exposure, _, agent) = await RunningWithAPositionAsync();
        using var disposing = application;
        using var browser = client;

        var (path, page) = await client.StopAgentAsync(agent, AgentPages.KeepPosition);

        Assert.Equal(AgentBrowser.AgentStatePage(agent), path);
        Assert.Equal(("Stopped", "UserStoppedKeepingPosition", Morning.AddMinutes(5)), AgentControlTests.StateOf(application, agent));
        Assert.Contains(
            "<p>You stopped the agent, and chose to keep its position with its stop-loss and take-profit orders.</p>", page);

        // Nothing was asked of what keeps positions: the position stays as it is.
        Assert.Empty(exposure.Closes);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentPositionCloseRequested");
        Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentStopped");

        // It can be closed at any time: the page of the stopped agent and the list lead to the
        // control that closes it (FR-AGT-12).
        var close = $"<a href=\"{AgentBrowser.ClosePositionPage(agent)}\">";

        Assert.Contains($"{close}{AgentPages.ClosePositionNow}</a>", page);
        Assert.Contains($"{close}{AgentPages.ClosePositionNow} for Holding</a>", await client.PageAsync(AgentBrowser.AgentsPage));

        // While it has the position, the agent is neither given another market nor deleted (FR-AGT-8).
        Assert.Contains(AgentPages.StructureLocked, await client.PageAsync(AgentBrowser.AgentPage(agent)));
        Assert.Contains(
            "This agent has an open position or open orders. Close them before you delete it.",
            (await client.DeleteAgentAsync(agent)).Page);
    }

    [Fact]
    public async Task An_agent_whose_position_could_not_be_closed_is_stopped_all_the_same_and_its_user_is_told()
    {
        var (application, client, exposure, _, agent) = await RunningWithAPositionAsync();
        using var disposing = application;
        using var browser = client;

        // The exchange gives no answer when the position is to be closed.
        exposure.CloseAnswer = _ => PositionCloseOutcome.Failed;

        var (path, page) = await client.StopAgentAsync(agent, AgentPages.ClosePosition);

        // The agent is stopped: it opens nothing from now on. The page says that the position
        // is open still, and leads to the control that closes it. It has no form to stop again.
        Assert.Equal(AgentBrowser.StopAgentPage(agent), path);
        Assert.Equal(("Stopped", "UserStoppedClosingPosition", Morning.AddMinutes(5)), AgentControlTests.StateOf(application, agent));
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">The agent is stopped. {AgentPages.NotClosed}</p>", page);
        Assert.Contains($"<a href=\"{AgentBrowser.ClosePositionPage(agent)}\">{AgentPages.ClosePositionNow}</a>", page);
        Assert.DoesNotContain(StopButton, page);
        Assert.Equal([agent], exposure.Closes);

        // The log says so, as a warning, by ids.
        var warned = Assert.Single(application.Events(), written => written.Level == "Warning"
            && written.Template.StartsWith("The position of agent {AgentId} could not be closed at market", StringComparison.Ordinal));
        Assert.Equal(agent.ToString(), warned.Text("AgentId"));

        // The control closes it once the exchange answers again.
        exposure.CloseAnswer = null;
        var (_, closed) = await client.ClosePositionAsync(agent);

        Assert.Contains("The position was closed at market.", closed);
        Assert.Equal([agent, agent], exposure.Closes);
        Assert.Equal("Stopped", AgentControlTests.StateOf(application, agent).State);
    }

    [Fact]
    public async Task Stopping_an_agent_that_has_no_open_position_asks_for_no_choice_and_closes_nothing()
    {
        var exposure = new StandInExposure();
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock, exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // One agent has open orders and no position: an entry that has not filled. Another holds nothing.
        var withOrders = await application.GiveAgentAsync(user.Id, Morning, name: "With orders", state: AgentState.Running);
        var holdingNothing = await application.GiveAgentAsync(user.Id, Morning, name: "Holding nothing", state: AgentState.Paused, symbol: "SOLUSDT");
        exposure.HoldsOrders(withOrders);

        foreach (var agent in new[] { withOrders, holdingNothing })
        {
            var page = await client.PageAsync(AgentBrowser.StopAgentPage(agent));

            Assert.Contains("<p>The agent has no open position.</p>", page);
            Assert.DoesNotContain("type=\"radio\"", page);

            // A choice that is posted all the same is no choice of anything: nothing is closed.
            var (path, _) = await client.PostAsync(
                AgentBrowser.StopAgentPage(agent), AgentBrowser.StopForm, Browser.AntiforgeryTokenIn(page), ("Position", AgentPages.ClosePosition));

            Assert.Equal(AgentBrowser.AgentStatePage(agent), path);
            Assert.Equal(("Stopped", "UserStopped", Morning), AgentControlTests.StateOf(application, agent));
        }

        Assert.Empty(exposure.Closes);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentPositionCloseRequested");
        Assert.Equal(2, StoredAuditEvent.All(application.Database).Count(recorded => recorded.Action == "AgentStopped"));
    }

    [Fact]
    public async Task A_position_that_opened_since_the_page_was_shown_is_asked_about_before_the_agent_is_stopped()
    {
        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, state: AgentState.Running);

        // The page showed no position, and has no choice. Then an entry fills.
        var page = await client.PageAsync(AgentBrowser.StopAgentPage(agent));
        Assert.DoesNotContain("type=\"radio\"", page);
        exposure.Holds(agent);

        // What the agent holds is asked again when the form arrives. It is not stopped: the
        // form comes back with the choice, which is then made.
        var (path, answer) = await client.PostAsync(AgentBrowser.StopAgentPage(agent), AgentBrowser.StopForm, Browser.AntiforgeryTokenIn(page));

        Assert.Equal(AgentBrowser.StopAgentPage(agent), path);
        Assert.Contains(ChooseFirst, answer);
        Assert.Equal(2, CountOf(answer, "type=\"radio\" name=\"Position\""));
        Assert.Equal("Running", AgentControlTests.StateOf(application, agent).State);

        await client.PostAsync(
            AgentBrowser.StopAgentPage(agent), AgentBrowser.StopForm, Browser.AntiforgeryTokenIn(answer), ("Position", AgentPages.KeepPosition));

        Assert.Equal("UserStoppedKeepingPosition", AgentControlTests.StateOf(application, agent).Cause);
        Assert.Empty(exposure.Closes);
    }

    [Fact]
    public async Task Pausing_an_agent_leaves_its_open_position_with_its_protective_orders()
    {
        var (application, client, exposure, _, agent) = await RunningWithAPositionAsync();
        using var disposing = application;
        using var browser = client;

        // Pausing asks nothing about the position, and nothing to be done with it: it stays
        // open with its protective orders (SRS 3.3).
        var (_, paused) = await client.PauseAgentAsync(agent);

        Assert.Equal(("Paused", "UserPaused", Morning.AddMinutes(5)), AgentControlTests.StateOf(application, agent));
        Assert.Empty(exposure.Closes);
        Assert.Contains(AgentPages.Explain(AgentState.Paused), AgentBrowser.Read(paused));

        // The position is still the agent's, and can be closed at any time. So when it is
        // resumed: the team manages the position again.
        Assert.Contains($"<a href=\"{AgentBrowser.ClosePositionPage(agent)}\">{AgentPages.ClosePositionNow}</a>", paused);

        await client.ResumeAgentAsync(agent);

        Assert.Equal("Running", AgentControlTests.StateOf(application, agent).State);
        Assert.Empty(exposure.Closes);
    }

    /// <summary>
    /// The application with a user who is signed in and has an agent that runs, started on its
    /// page, with an open position. The clock is five minutes past the start.
    /// </summary>
    private static async Task<(AccountsApplication Application, HttpClient Client, StandInExposure Exposure, Guid UserId, Guid AgentId)>
        RunningWithAPositionAsync(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? host = null)
    {
        var exposure = new StandInExposure();
        var clock = new TestClock(Morning);
        var application = AgentsApplication.With(new StandInMarketData(), clock, exposure, host: host);
        var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Holding", market: nameof(Market.Futures)));
        var agent = Assert.Single(StoredAgent.All(application.Database));
        await client.StartAgentAsync(agent.Id);
        exposure.Holds(agent.Id);
        clock.Set(Morning.AddMinutes(5));

        return (application, client, exposure, user.Id, agent.Id);
    }

    private static int CountOf(string page, string text) => page.Split(text).Length - 1;
}
