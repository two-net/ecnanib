using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// A "Close position now" control is available at all times for any agent with an open position
/// (FR-AGT-12): in whatever state the agent is, and whatever its key is. Agents places no order:
/// it records the request and asks whatever keeps positions, which a stand-in is here, because
/// positions are not built yet.
/// </summary>
public class AgentClosePositionTests
{
    private const string Ada = "ada@example.com";

    private const string CloseButton = "<button type=\"submit\">Close position now</button>";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentState.Running)]
    [InlineData(AgentState.Paused)]
    [InlineData(AgentState.Halted)]
    [InlineData(AgentState.AttentionRequired)]
    [InlineData(AgentState.Stopped)]
    public async Task The_control_is_there_for_an_agent_with_an_open_position_in_whatever_state_it_is(AgentState state)
    {
        var exposure = new StandInExposure();
        var clock = new TestClock(Morning.AddHours(2));
        using var application = AgentsApplication.With(new StandInMarketData(), clock, exposure, host: host => host.From("203.0.113.7"));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The user has stored no key at all: closing a position asks nothing of the key. The
        // test stores the agent in its state, with a position.
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Holding", state: state);
        exposure.Holds(agent);
        var stored = application.RowsOf(user.Id);

        // The list and the page of the agent's state lead to the control, which is called the
        // same wherever it is.
        var closePage = AgentBrowser.ClosePositionPage(agent);

        Assert.Contains($"<a href=\"{closePage}\">Close position now for Holding</a>", await client.PageAsync(AgentBrowser.AgentsPage));
        Assert.Contains($"<a href=\"{closePage}\">Close position now</a>", await client.PageAsync(AgentBrowser.AgentStatePage(agent)));

        // Its page asks once more, says what closing does, and that the state stays as it is.
        var page = AgentBrowser.Read(await client.PageAsync(closePage));

        Assert.Contains("<h1>Close the position of the agent Holding</h1>", page);
        Assert.Contains("It cannot be undone.", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"The state of the agent stays as it is: <strong>{AgentPages.Describe(state)}</strong>.", page);
        Assert.Contains(CloseButton, page);
        Assert.Contains($"<a href=\"{AgentBrowser.AgentStatePage(agent)}\">Keep the position</a>", page);
        Assert.Empty(exposure.Closes);

        // The button closes the position, and the page says so.
        var (path, closed) = await client.ClosePositionAsync(agent);

        Assert.Equal(closePage, path);
        // With the state of the agent, which is as it was, and what that state means (NFR-USA-2).
        Assert.Contains(
            $"<p class=\"form-done\" role=\"status\">The position was closed at market. The agent is {AgentPages.Describe(state)}, " +
            $"as it was. {AgentPages.Explain(state)}</p>",
            AgentBrowser.Read(closed));
        Assert.DoesNotContain(CloseButton, closed);
        Assert.Equal([agent], exposure.Closes);

        // The agent is in the state it was in: closing a position changes none.
        Assert.Equal(stored["agents"], application.RowsOf(user.Id)["agents"]);
        Assert.Equal(stored["agent_state_changes"], application.RowsOf(user.Id)["agent_state_changes"]);

        // The request is in the audit trail, by the id of the agent (NFR-LOG-7).
        var recorded = Assert.Single(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));

        Assert.Equal(
            ("AgentPositionCloseRequested", $"agent:{agent}", Morning.AddHours(2), user.Id, "203.0.113.7"),
            (recorded.Action, recorded.Target, recorded.OccurredAt, recorded.UserId, recorded.Origin));

        // With the position gone, so is the control, on the list and on the page of the state.
        Assert.DoesNotContain(AgentPages.ClosePositionNow, await client.PageAsync(AgentBrowser.AgentsPage));
        Assert.DoesNotContain(AgentPages.ClosePositionNow, await client.PageAsync(AgentBrowser.AgentStatePage(agent)));
    }

    [Fact]
    [ShowsAuditOf(AuditAction.AgentPositionCloseRequested)]
    public async Task Closing_a_position_by_hand_is_recorded_when_it_is_asked_for_and_only_then()
    {
        var exposure = new StandInExposure();
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock, exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, state: AgentState.Running);
        exposure.Holds(agent);

        // Opening the page that asks once more records nothing, and asks for nothing.
        var page = await client.PageAsync(AgentBrowser.ClosePositionPage(agent));

        Assert.Empty(Requests(application));
        Assert.Empty(exposure.Closes);

        // The request is recorded before the position is closed: it is in the trail when the
        // close is asked for, so that it stands whatever becomes of the order.
        var recordedAtClose = -1;
        exposure.OnClose = _ => recordedAtClose = Requests(application).Count;

        clock.Set(Morning.AddMinutes(3));
        await client.PostAsync(AgentBrowser.ClosePositionPage(agent), AgentBrowser.ClosePositionForm, Browser.AntiforgeryTokenIn(page));

        Assert.Equal(1, recordedAtClose);
        Assert.Equal([$"agent:{agent} {Morning.AddMinutes(3):O}"], Requests(application));

        // The form is sent a second time. Nothing is open by then: nothing is asked for, and
        // nothing is recorded.
        clock.Set(Morning.AddMinutes(4));
        var (_, again) = await client.PostAsync(
            AgentBrowser.ClosePositionPage(agent), AgentBrowser.ClosePositionForm, Browser.AntiforgeryTokenIn(page));

        Assert.Contains($"<p role=\"status\">{AgentPages.NothingOpen}</p>", again);
        Assert.Single(Requests(application));
        Assert.Equal([agent], exposure.Closes);

        // The log tells of the request, by ids, without the name of the action.
        Assert.Single(
            application.Events(),
            written => written.Template == "User {UserId} asked to close the position of agent {AgentId} at market: {PositionCloseOutcome}");
        Assert.DoesNotContain(application.Events(), written => written.ToString().Contains("AgentPositionCloseRequested", StringComparison.Ordinal));

        static IReadOnlyList<string> Requests(AccountsApplication application) =>
        [
            .. StoredAuditEvent.All(application.Database)
                .Where(recorded => recorded.Action == "AgentPositionCloseRequested")
                .Select(recorded => $"{recorded.Target} {recorded.OccurredAt:O}"),
        ];
    }

    [Fact]
    public async Task An_agent_with_no_open_position_has_no_such_control()
    {
        var exposure = new StandInExposure();
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // A draft never held anything. A running agent holds nothing yet, and another has an
        // entry that has not filled: open orders, and no position.
        var draft = await application.GiveAgentAsync(user.Id, Morning, name: "Draft");
        var running = await application.GiveAgentAsync(user.Id, Morning, name: "Running", state: AgentState.Running, symbol: "SOLUSDT");
        var withOrders = await application.GiveAgentAsync(
            user.Id, Morning, name: "With orders", state: AgentState.Running, market: Shared.Market.Futures);
        exposure.Holds(draft);
        exposure.HoldsOrders(withOrders);

        Assert.DoesNotContain(AgentPages.ClosePositionNow, await client.PageAsync(AgentBrowser.AgentsPage));

        foreach (var agent in new[] { draft, running, withOrders })
        {
            Assert.DoesNotContain(AgentPages.ClosePositionNow, await client.PageAsync(AgentBrowser.AgentStatePage(agent)));

            // The page of the control says that nothing is open, and has no button.
            var page = await client.PageAsync(AgentBrowser.ClosePositionPage(agent));

            Assert.Contains($"<p role=\"status\">{AgentPages.NothingOpen}</p>", page);
            Assert.DoesNotContain(CloseButton, page);
        }

        // A form that is posted all the same closes nothing, and records nothing.
        var token = Browser.AntiforgeryTokenIn(await client.PageAsync(AgentBrowser.AgentsPage));
        var (path, answer) = await client.PostAsync(AgentBrowser.ClosePositionPage(running), AgentBrowser.ClosePositionForm, token);

        Assert.Equal(AgentBrowser.ClosePositionPage(running), path);
        Assert.Contains($"<p role=\"status\">{AgentPages.NothingOpen}</p>", answer);
        Assert.Empty(exposure.Closes);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_position_that_could_not_be_closed_is_said_to_be_open_still_and_can_be_closed_again()
    {
        var exposure = new StandInExposure { CloseAnswer = _ => PositionCloseOutcome.Failed };
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), exposure);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, state: AgentState.Paused);
        exposure.Holds(agent);

        // The exchange gives no answer. The page says that the position is open as it was, and
        // the button is there to try again.
        var (path, failed) = await client.ClosePositionAsync(agent);

        Assert.Equal(AgentBrowser.ClosePositionPage(agent), path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.NotClosed}</p>", failed);
        Assert.Contains(CloseButton, failed);
        Assert.DoesNotContain("The position was closed", failed);
        Assert.Equal("Paused", AgentControlTests.StateOf(application, agent).State);

        // Tried again when the exchange answers, it is closed. Each request is in the trail.
        exposure.CloseAnswer = null;
        var (_, closed) = await client.ClosePositionAsync(agent);

        Assert.Contains("The position was closed at market.", closed);
        Assert.Equal([agent, agent], exposure.Closes);
        Assert.Equal(2, StoredAuditEvent.All(application.Database).Count(recorded => recorded.Action == "AgentPositionCloseRequested"));
    }
}
