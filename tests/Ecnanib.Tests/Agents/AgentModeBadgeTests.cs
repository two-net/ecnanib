using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.AspNetCore.Components;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// A Demo or Live badge is always visible on the screens of an agent (FR-UI-3), and what an
/// agent did in Demo is labeled as Demo wherever it is shown, also after the agent was switched
/// to Live (FR-BIN-11).
/// </summary>
public class AgentModeBadgeTests
{
    private const string Ada = "ada@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Every_page_of_an_agent_shows_the_badge_of_its_mode()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The user has just proved who they are, so that the page which switches an agent to
        // Live is shown to them, and not the page that asks for the proof.
        await client.ConfirmIdentityAsync();

        var inDemo = await application.GiveAgentAsync(user.Id, Morning, name: "In Demo", state: AgentState.Stopped);
        var inLive = await application.GiveAgentAsync(
            user.Id, Morning, name: "In Live", state: AgentState.Stopped, symbol: "SOLUSDT", mode: TradingMode.Live);

        // Each has a cycle, in the mode that it is in: the timeline of a cycle is a page of its agent.
        var cycles = new Dictionary<Guid, Guid>
        {
            [inDemo] = await application.GiveCycleAsync(user.Id, inDemo, Morning, orders: false),
            [inLive] = await application.GiveCycleAsync(user.Id, inLive, Morning, orders: false),
        };

        // Every page of the agents, by its route: a page that is added later is among them, and
        // fails here until it shows the badge.
        var routes = typeof(AgentPages).Assembly.GetTypes()
            .Where(page => page.Namespace == typeof(AgentPages).Namespace)
            .SelectMany(page => page.GetCustomAttributes<RouteAttribute>())
            .Select(route => route.Template)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("/agents/{Id:guid}/state", routes);
        Assert.Contains("/agents/{Id:guid}/live", routes);

        var ofAnAgent = routes.Where(route => route.Contains("{Id:guid}", StringComparison.Ordinal)).ToList();
        var ofNoAgent = routes.Except(ofAnAgent).ToList();

        foreach (var (agent, mode, other) in new[]
        {
            (inDemo, TradingMode.Demo, TradingMode.Live),
            (inLive, TradingMode.Live, TradingMode.Demo),
        })
        {
            foreach (var route in ofAnAgent)
            {
                var path = route
                    .Replace("{Id:guid}", agent.ToString(), StringComparison.Ordinal)
                    .Replace("{CycleId:guid}", cycles[agent].ToString(), StringComparison.Ordinal)
                    .Replace("{Control}", "start", StringComparison.Ordinal);

                Assert.False(
                    path.Contains('{', StringComparison.Ordinal),
                    $"The route {route} has a parameter that this test does not fill. Say here what a path of it is.");

                var page = await client.PageAsync(path);

                // Under the heading, where it is seen before anything else of the page, and in
                // the mode of this agent alone.
                Assert.True(
                    Regex.IsMatch(page, $@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(mode))} "),
                    $"The page {path} does not show the badge of {mode} under its heading:{Environment.NewLine}{page}");
                Assert.DoesNotContain(AgentBrowser.Badge(other), page);
            }
        }

        // The pages of no one agent: the list, which has the badge of each agent in its row, and
        // the form that creates an agent, which is in Demo.
        Assert.Equal(["/agents", "/agents/new"], ofNoAgent);

        var list = await client.PageAsync("/agents");

        Assert.Contains($"<td>{AgentBrowser.Badge(TradingMode.Demo)}</td>", list);
        Assert.Contains($"<td>{AgentBrowser.Badge(TradingMode.Live)}</td>", list);

        var form = await client.PageAsync("/agents/new");

        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", form);
        Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Live), form);
    }

    [Fact]
    public async Task The_changes_of_a_state_say_the_mode_they_happened_in()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();

        // An agent is created, started, and stopped in Demo.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"));
        await application.GiveTeamsAsync(user.Id, Morning);
        var agent = Assert.Single(StoredAgent.All(application.Database)).Id;
        clock.Set(Morning.AddMinutes(1));
        await client.StartAgentAsync(agent);
        clock.Set(Morning.AddMinutes(2));
        await client.StopAgentAsync(agent);

        // It is switched to Live, and started there.
        clock.Set(Morning.AddMinutes(3));
        await client.SwitchToLiveAsync(agent);
        clock.Set(Morning.AddMinutes(4));
        await client.ConfirmControlAsync(agent, "start");

        // What it did in Demo is stored as Demo, and stays so: the switch relabels nothing.
        Assert.Equal(
            ["Demo Draft", "Demo Running", "Demo Stopped", "Live Running"],
            StoredAgentStateChange.Of(application.Database, agent).Select(change => $"{change.Mode} {change.State}"));

        // The page of its state says the mode of each change, latest first, beside the badge of
        // the mode that the agent is in now.
        var page = await client.PageAsync(AgentBrowser.AgentStatePage(agent));

        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Live))} ", page);
        Assert.Contains("<th scope=\"col\">Mode</th>", page);
        Assert.Equal(["Live", "Demo", "Demo", "Demo"], AgentControlTests.ModesOfChangesOn(page));
        Assert.Equal(
            ["Running", "Stopped", "Running", "Draft"],
            AgentControlTests.ChangesOn(page).Select(change => change.State));

        // So does the export of the user's data.
        Assert.Equal(["Demo Draft", "Demo Running", "Demo Stopped", "Live Running"], await ExportedChangesAsync(client));

        // Stopped and switched back to Demo, what it did in Live stays labeled as Live.
        clock.Set(Morning.AddMinutes(5));
        await client.StopAgentAsync(agent);
        clock.Set(Morning.AddMinutes(6));
        await client.SwitchToDemoAsync(agent);

        Assert.Equal("Demo", StoredAgent.All(application.Database).Single().Mode);
        Assert.Equal(
            ["Demo Draft", "Demo Running", "Demo Stopped", "Live Running", "Live Stopped"],
            StoredAgentStateChange.Of(application.Database, agent).Select(change => $"{change.Mode} {change.State}"));
        Assert.Equal(
            ["Live", "Live", "Demo", "Demo", "Demo"],
            AgentControlTests.ModesOfChangesOn(await client.PageAsync(AgentBrowser.AgentStatePage(agent))));
        Assert.Equal(
            ["Demo Draft", "Demo Running", "Demo Stopped", "Live Running", "Live Stopped"], await ExportedChangesAsync(client));
    }

    /// <summary>The changes of the states of the user's agents in the export of their data: each as its mode and its state, in order.</summary>
    private static async Task<IReadOnlyList<string>> ExportedChangesAsync(HttpClient client)
    {
        var export = JsonDocument.Parse(await client.PageAsync("/account/export")).RootElement;

        return
        [
            .. export.GetProperty("agentStateChanges").EnumerateArray()
                .Select(change => $"{change.GetProperty("mode").GetString()} {change.GetProperty("state").GetString()}"),
        ];
    }
}
