using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Experience;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Notifications;
using Ecnanib.WebUi.Pages;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Live;
using Ecnanib.WebUi.Pages.Notifications;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.LiveUpdates;

/// <summary>
/// The pages that follow what changes (FR-NTF-2): each carries one part for which it makes its
/// connection, which is the one push channel from the application to the browser (NFR-ARC-6),
/// and is written with what that part shows then: the steps of a cycle that runs, the team of
/// an agent at work as a pipeline on the screen of the agent (FR-ANI-1), and the position of an
/// agent with the price that it is valued at. See docs/live-updates.md and docs/agent-screen.md.
/// </summary>
/// <remarks>
/// A page is asked for as a browser asks for it, and read as it was written: what its
/// connection does afterwards is in <see cref="LiveFeedTests"/>.
/// </remarks>
public partial class LivePageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly LivePrice Bitcoin = new(Market.Spot, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.LastTraded);
    private static readonly LivePrice BitcoinMark = new(Market.Futures, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.Mark);

    [Fact]
    public async Task Every_page_that_follows_carries_one_part_for_its_connection_and_no_other_page_carries_one()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        var cycle = await application.GiveCycleAsync(ada.User.Id, agent, Feeding.Morning);

        foreach (var path in Following(agent, cycle))
        {
            var page = await ada.Browser.PageAsync(path);

            // One part, which the framework marks where the page is to make its connection
            // for it. It is told apart by a key, so that it stays when the page asks for
            // itself again, and is another one on another page.
            var part = Assert.Single(ConnectedPart().Matches(page));

            // The key is the path of the page.
            var key = Key().Match(part.Value);

            Assert.True(
                key.Success && key.Groups[1].Value == path,
                $"The part of {path} that follows is not told apart by the path of its page, so it would be made anew each time " +
                $"the page asks for itself again: {part.Value}");
        }

        foreach (var path in new[]
        {
            "/",
            "/account",
            WebUiModule.KeysPath,
            WebUiModule.AppearancePath,
            WebUiModule.DeviceAppearancePath,
            WebUiModule.CatalogPath,
            WebUiModule.ExperiencePath,
            WebUiModule.UsagePath,
            WebUiModule.AgentPromptsPath(agent),
            WebUiModule.AgentRiskChecksPath(agent),
            WebUiModule.StopAgentPath(agent),
        })
        {
            // A page that follows nothing makes no connection.
            Assert.Empty(ConnectedPart().Matches(await ada.Browser.PageAsync(path)));
        }
    }

    [Fact]
    public async Task No_page_that_follows_has_a_field_that_its_user_types_in()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        var cycle = await application.GiveCycleAsync(ada.User.Id, agent, Feeding.Morning);
        await application.GiveNotificationAsync(ada.User.Id, agent, Feeding.Morning);

        foreach (var path in Following(agent, cycle))
        {
            var page = await ada.Browser.PageAsync(path);

            // A page that asks for itself again is written anew around its user, and what they
            // had typed into a field would be gone. So such a page has buttons, and no fields.
            Assert.All(Input().Matches(page), input => Assert.Contains("type=\"hidden\"", input.Value, StringComparison.Ordinal));
            Assert.DoesNotContain("<textarea", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<select", page, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_page_of_the_state_of_an_agent_says_its_open_position_with_the_price_that_the_watch_has()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var holds = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Holds");
        var shorts = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Shorts", market: Market.Futures);
        var idle = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Idle", symbol: "SOLUSDT");

        await application.GiveTradeAsync(ada.User.Id, holds, Feeding.Morning, holdingSeconds: 600, closed: false);
        await application.GiveTradeAsync(ada.User.Id, shorts, Feeding.Morning, holdingSeconds: 600, closed: false);

        // A price that nobody follows is not known, and the page asks Binance for none: it is
        // written with what there is.
        var page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(holds)));

        Assert.Contains("<h2>Position</h2>", page);
        Assert.Contains("<p>A position of 0.01 in BTCUSDT, entered at 100000 USDT, open since 24 November 2026, 08:50 UTC.</p>", page);
        Assert.Contains("<p>No stop-loss and no take-profit rest on the exchange for it.</p>", page);
        Assert.Contains("<p class=\"live-price\">The price of BTCUSDT is not known at the moment.</p>", page);
        Assert.DoesNotContain("At that price", page);

        // Another page follows the price by now, and the watch has read it.
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Feeding.Morning.AddSeconds(7)));
        feeding.Prices.Say(BitcoinMark, new WatchedPrice(99_400.5m, Feeding.Morning.AddSeconds(8)));

        page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(holds)));

        Assert.Contains("<p class=\"live-price\">BTCUSDT last traded at 100250 USDT, as of 24 November 2026, 09:00:07 UTC.</p>", Plain(page));
        Assert.Contains("<p class=\"live-unrealized\">At that price the position would make 2.5 USDT, before fees and funding.</p>", Plain(page));

        // The price and what the position would make are figures: each number is in an element
        // of its own, which says its value, so it counts from its old value to its new one
        // when the connection of the page writes another (FR-ANI-2).
        Assert.Contains("last traded at <data class=\"figure\" value=\"100250\">100250</data> USDT, as of", page);
        Assert.Contains("would make <data class=\"figure\" value=\"2.5\">2.5</data> USDT, before", page);

        // A position of the Futures market says its side, and is valued at the mark price. What
        // it would make is said in words, and a loss as a loss.
        page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(shorts)));

        Assert.Contains("<p>A long position of 0.01 in BTCUSDT, entered at 100000 USDT, open since 24 November 2026, 08:50 UTC.</p>", page);
        Assert.Contains("<p class=\"live-price\">The mark price of BTCUSDT is 99400.5 USDT, as of 24 November 2026, 09:00:08 UTC.</p>", Plain(page));
        Assert.Contains("<p class=\"live-unrealized\">At that price the position would lose 5.995 USDT, before fees and funding.</p>", Plain(page));

        // The value of a loss is less than nothing, though its number is written without a
        // sign: the words say the loss, and the figure does not count across the two.
        Assert.Contains("would lose <data class=\"figure\" value=\"-5.995\">5.995</data> USDT, before", page);

        // An agent that holds nothing says so, with the price of its own symbol, when one is known.
        page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(idle)));

        Assert.Contains("<p>The agent holds no position.</p>", page);
        Assert.Contains("<p class=\"live-price\">The price of SOLUSDT is not known at the moment.</p>", page);
        Assert.DoesNotContain("BTCUSDT last traded", page);
    }

    [Fact]
    public async Task The_page_of_an_agent_says_the_protection_of_its_position_as_it_rests_on_the_exchange()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        var position = await application.GiveTradeAsync(ada.User.Id, agent, Feeding.Morning, closed: false);

        await application.GiveProtectionAsync(ada.User.Id, agent, position, OrderPurpose.StopLoss, 99_000m, Feeding.Morning);

        Assert.Contains(
            "<p>Its stop-loss rests on the exchange at 99000. No take-profit rests there for it.</p>",
            AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent))));

        await application.GiveProtectionAsync(ada.User.Id, agent, position, OrderPurpose.TakeProfit, 102_000m, Feeding.Morning);

        Assert.Contains(
            "<p>Its stop-loss rests on the exchange at 99000, and its take-profit at 102000.</p>",
            AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent))));
    }

    [Fact]
    public async Task A_cycle_that_runs_says_its_steps_on_the_pages_of_its_agent_and_on_no_other()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Runs");
        var other = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, name: "Other");
        var hers = await application.GiveAgentAsync(grace.User.Id, Feeding.Morning, name: "Hers");

        string[] ofAgent = [WebUiModule.AgentStatePath(agent), WebUiModule.AgentCyclesPath(agent), WebUiModule.AgentTestCyclePath(agent)];

        // No cycle runs: there is nothing to say.
        foreach (var path in ofAgent)
        {
            Assert.DoesNotContain("is running", await ada.Browser.PageAsync(path));
        }

        // A cycle of the agent runs, and has come to its third step.
        feeding.Progress.Runs(ada.User.Id, agent, Feeding.Cycle(CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts));

        foreach (var path in ofAgent)
        {
            var page = AgentBrowser.Read(await ada.Browser.PageAsync(path));

            Assert.Contains("<h2>A cycle is running</h2>", page);
            Assert.Contains("It is for the candle that closed at 24 November 2026, 09:00 UTC, and started at 24 November 2026, 09:00:00 UTC.", page);
            Assert.Equal(
                [
                    "Orders settled with the exchange, and loss limits evaluated: done",
                    "Data package: done",
                    "Market Analyst and Sentiment Analyst: <strong>now, since 24 November 2026, 09:00:00 UTC</strong>",
                ],
                Step().Matches(page).Select(step => step.Groups[1].Value));
        }

        // The pages of another agent of its user, and of an agent of another user, say nothing of it.
        Assert.DoesNotContain("is running", await ada.Browser.PageAsync(WebUiModule.AgentStatePath(other)));
        Assert.DoesNotContain("is running", await ada.Browser.PageAsync(WebUiModule.AgentsPath));
        Assert.DoesNotContain("is running", await grace.Browser.PageAsync(WebUiModule.AgentStatePath(hers)));

        // And the page of the agent is no page of another user: it is not found, as before.
        using var notHers = await grace.Browser.GetAsync(WebUiModule.AgentStatePath(agent), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, notHers.StatusCode);
        Assert.DoesNotContain("is running", await notHers.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A test cycle beside it is told apart, and says that it places no orders.
        feeding.Progress.Runs(
            ada.User.Id,
            agent,
            Feeding.Cycle(CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts),
            Feeding.Cycle(CycleStep.DataPackage) with { Kind = CycleKind.Test });

        var both = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentTestCyclePath(agent)));

        Assert.Contains("<h2>A cycle is running</h2>", both);
        Assert.Contains("<h2>A test cycle is running</h2>", both);
        Assert.Contains("You ran it as a test: it places no orders.", both);
        Assert.Equal(4, Step().Matches(both).Count);
    }

    [Fact]
    public async Task The_page_of_the_state_of_an_agent_says_that_its_team_is_at_rest_while_no_cycle_of_it_runs()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);

        // The screen of the agent says what its team does now, also when it does nothing
        // (FR-RPT-2): one part, with the team, of which every role waits, and that it is at
        // rest. The other pages that show the cycles which run have nothing to say then.
        var resting = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));
        var part = Assert.Single(LivePart().Matches(resting)).Groups[1].Value;

        Assert.EndsWith("</ol><p class=\"live-rest\">The team is at rest: no cycle of the agent runs at the moment.</p>", part);
        Assert.All(TeamPipeline.CardsOf(Assert.Single(TeamPipeline.On(part))), role => Assert.Equal("waiting", role.State));

        foreach (var path in new[] { WebUiModule.AgentCyclesPath(agent), WebUiModule.AgentTestCyclePath(agent), WebUiModule.AgentsPath, WebUiModule.DashboardPath })
        {
            Assert.DoesNotContain("at rest", await ada.Browser.PageAsync(path));
        }

        // A cycle of the agent runs: its steps are said in place of that.
        feeding.Progress.Runs(ada.User.Id, agent, Feeding.Cycle(CycleStep.Settling, CycleStep.DataPackage));

        var running = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));

        Assert.Contains("<h2>A cycle is running</h2>", running);
        Assert.DoesNotContain("at rest", running);
    }

    [Fact]
    public async Task The_screen_of_an_agent_shows_its_team_at_work_as_a_pipeline_and_no_other_page_does()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        var model = $"{StoredAgentRows.TeamModel}, of Claude";

        // No cycle runs. The four roles are there all the same, in the order that they work in,
        // each with its model and the provider of the model: each waits, and nothing was handed on.
        var page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));
        var pipeline = Assert.Single(TeamPipeline.On(page));

        Assert.Equal(
            [
                ("Market Analyst", model, "waiting", "role-waiting"),
                ("Sentiment Analyst", model, "waiting", "role-waiting"),
                ("Strategist", model, "waiting", "role-waiting"),
                ("Risk Manager", model, "waiting", "role-waiting"),
            ],
            TeamPipeline.CardsOf(pipeline));
        Assert.Equal([false, false, false], TeamPipeline.HandedOf(pipeline));

        // The two analysts are one stage, which they share: they work at the same time.
        Assert.Matches("<li class=\"pipeline-stage\"><ul class=\"pipeline-parallel\"><li><div class=\"pipeline-role[^>]*>.*?</div></li><li><div", Flat(pipeline));

        // The page says what the four words mean, above the team.
        Assert.Contains("waiting until the cycle comes to it, working while its model is called, done when it gave a valid result, and failed when it gave none.", page);

        // A cycle runs: one analyst is done while the other works, and the others wait.
        var cycle = Feeding.Cycle(CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts);

        feeding.Progress.Runs(ada.User.Id, agent, cycle with { Roles = Feeding.Roles(RoleState.Done, RoleState.Working) });

        page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));
        pipeline = Assert.Single(TeamPipeline.On(page));

        Assert.Equal(
            [("done", "role-done"), ("working", "role-working"), ("waiting", "role-waiting"), ("waiting", "role-waiting")],
            TeamPipeline.CardsOf(pipeline).Select(role => (role.State, role.Class)));
        Assert.Equal([false, false, false], TeamPipeline.HandedOf(pipeline));

        // It is in the part of its cycle, above the steps, which are said as words as before.
        Assert.Matches(
            "<section class=\"live-cycle\" aria-live=\"polite\"><ol class=\"team-pipeline\".*?</ol><h2>A cycle is running</h2>.*?<ol class=\"cycle-steps\">",
            Flat(page));
        Assert.Equal(3, Step().Matches(page).Count);

        // The Sentiment Analyst has failed, and the cycle goes on: the work is handed to the
        // Strategist, and the line into its stage says so. The Risk Manager waits.
        feeding.Progress.Runs(
            ada.User.Id,
            agent,
            cycle with { Roles = Feeding.Roles(RoleState.Done, RoleState.Failed, RoleState.Working) });

        pipeline = Assert.Single(TeamPipeline.On(AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)))));

        Assert.Equal(
            [("done", "role-done"), ("failed", "role-failed"), ("working", "role-working"), ("waiting", "role-waiting")],
            TeamPipeline.CardsOf(pipeline).Select(role => (role.State, role.Class)));
        Assert.Equal([false, true, false], TeamPipeline.HandedOf(pipeline));

        // What moves is for the stylesheet: the part carries no style and no script of its own.
        Assert.DoesNotContain("style=", pipeline);
        Assert.DoesNotContain("<script", pipeline);

        // The other pages that show the cycles which run say their steps, and have no pipeline.
        foreach (var path in new[] { WebUiModule.AgentCyclesPath(agent), WebUiModule.AgentTestCyclePath(agent) })
        {
            var other = AgentBrowser.Read(await ada.Browser.PageAsync(path));

            Assert.Contains("<h2>A cycle is running</h2>", other);
            Assert.Empty(TeamPipeline.On(other));
        }
    }

    [Fact]
    public async Task A_cycle_that_ended_a_moment_ago_is_on_the_screen_of_its_agent_with_how_each_role_ended_and_on_no_other_page()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);

        // The Strategist gave no valid result, so the cycle ended with no decision, and the
        // Risk Manager was never called. It ended as the page is asked for.
        feeding.Progress.Runs(
            ada.User.Id,
            agent,
            Feeding.Cycle(CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts, CycleStep.Strategist, CycleStep.Recording) with
            {
                Roles = Feeding.Roles(RoleState.Done, RoleState.Done, RoleState.Failed),
                EndedAt = Feeding.Morning,
            });

        var page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));
        var pipeline = Assert.Single(TeamPipeline.On(page));

        Assert.Equal(["done", "done", "failed", "waiting"], TeamPipeline.CardsOf(pipeline).Select(role => role.State));
        Assert.Equal([false, true, false], TeamPipeline.HandedOf(pipeline));

        // It says that it has ended, and when, and each of its steps is done: none is "now".
        Assert.Contains("<h2>A cycle has ended</h2>", page);
        Assert.Contains(
            "<p>It was for the candle that closed at 24 November 2026, 09:00 UTC, and ended at 24 November 2026, 09:00:00 UTC. Its timeline shows the rest.</p>",
            page);
        Assert.Equal(5, Step().Matches(page).Count);
        Assert.All(Step().Matches(page), step => Assert.EndsWith(": done", step.Groups[1].Value));
        Assert.DoesNotContain("is running", page);
        Assert.DoesNotContain("at rest", page);

        // The other pages show the cycles that run, and this one runs no longer.
        foreach (var path in new[] { WebUiModule.AgentCyclesPath(agent), WebUiModule.AgentTestCyclePath(agent) })
        {
            var other = await ada.Browser.PageAsync(path);

            Assert.DoesNotContain("A cycle has ended", other);
            Assert.DoesNotContain("is running", other);
        }

        // Once its moment is over, the screen says that the team is at rest, with every role waiting.
        feeding.Clock.Advance(RunningCycle.KeptAfterEnd);

        page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));

        Assert.Contains("The team is at rest", page);
        Assert.DoesNotContain("A cycle has ended", page);
        Assert.All(TeamPipeline.CardsOf(Assert.Single(TeamPipeline.On(page))), role => Assert.Equal("waiting", role.State));
    }

    [Fact]
    public async Task The_pipeline_stays_in_its_place_as_a_cycle_starts_goes_on_and_ends()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        await using var scope = application.Services.CreateAsyncScope();
        using var written = new Written(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());

        IReadOnlyList<LiveRole> team = [.. Enum.GetValues<Role>().Select(role => new LiveRole(role, "a-model, of Claude"))];
        var cycle = Feeding.Cycle(CycleStep.DataPackage, CycleStep.Analysts);

        // The team at rest: the part, and the pipeline in it.
        await written.ShowAsync([], team);

        var parts = written.Parts.ToList();

        Assert.Equal(2, parts.Count);

        // A cycle starts, goes on, has ended, and is kept no longer: the team is at rest again.
        await written.ShowAsync([cycle with { Roles = Feeding.Roles(RoleState.Working, RoleState.Working) }], team);
        await written.ShowAsync([cycle with { Roles = Feeding.Roles(RoleState.Done, RoleState.Failed, RoleState.Working) }], team);
        await written.ShowAsync([cycle with { Roles = Feeding.Roles(RoleState.Done, RoleState.Failed, RoleState.Done, RoleState.Done), EndedAt = Feeding.Morning }], team);
        await written.ShowAsync([], team);

        // The pipeline is the one that was written first, all along: none was taken away, and
        // none was written in its place. So the browser changes what a role does in the card
        // that is there, which is what lets the stylesheet show the change as one (FR-ANI-1).
        // A pipeline that was written anew would start in its new state, and nothing would move.
        Assert.Empty(written.TakenAway);
        Assert.Equal(parts, written.Parts);

        // A second cycle beside the first has a pipeline of its own, which goes with it.
        await written.ShowAsync([cycle, Feeding.Cycle(CycleStep.DataPackage) with { Kind = CycleKind.Test }], team);

        Assert.Equal(3, written.Parts.Count);

        await written.ShowAsync([cycle], team);

        Assert.Single(written.TakenAway);
        Assert.DoesNotContain(written.TakenAway[0], parts);
    }

    [Fact]
    public async Task A_part_of_a_page_that_fails_is_recorded_under_a_reference_and_says_the_reference_and_nothing_of_the_error()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        await using var scope = application.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());

        var written = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment failing = builder =>
            {
                builder.OpenComponent<Fails>(0);
                builder.CloseComponent();
            };

            var output = await renderer.RenderComponentAsync<LiveErrors>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(LiveErrors.ChildContent)] = failing }));

            return output.ToHtmlString();
        });

        // An error inside such a part passes nothing that records it otherwise.
        var recorded = Assert.Single(application.Events(), logged => logged.Level == "Error");

        Assert.Equal("An unhandled error was shown to the user as reference {ReferenceId}", recorded.Template);
        Assert.Contains(Fails.Secret, recorded.Exception);

        var reference = recorded.Text("ReferenceId");

        Assert.Matches("^[A-Z2-9]{10}$", reference);
        Assert.Equal(
            $"<p class=\"live-stopped\" role=\"alert\">{LivePages.Stopped(reference!)}</p>",
            written.Trim());
        Assert.DoesNotContain(Fails.Secret, written);
    }

    [Fact]
    public async Task Every_page_that_follows_offers_to_pause_under_its_heading_and_no_other_page_does()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        var cycle = await application.GiveCycleAsync(ada.User.Id, agent, Feeding.Morning);

        foreach (var path in Following(agent, cycle))
        {
            var page = await ada.Browser.PageAsync(path);

            // A page that changes by itself says so, with a link that stops it (NFR-USA-1):
            // one of them, which leads to the same page with the choice in its address.
            var pause = Assert.Single(PauseOn(page));

            Assert.Equal((LivePages.Follows, $"{path}?live=paused", LivePages.Pause), pause);

            // It stands in the content, after the heading of the page and ahead of what follows.
            var at = page.IndexOf("<p class=\"live-pause\">", StringComparison.Ordinal);

            Assert.InRange(at, page.IndexOf("</h1>", StringComparison.Ordinal), ConnectedPart().Match(page).Index);
        }

        foreach (var path in new[]
        {
            "/",
            "/account",
            WebUiModule.KeysPath,
            WebUiModule.CatalogPath,
            WebUiModule.ExperiencePath,
            WebUiModule.UsagePath,
            WebUiModule.AgentPromptsPath(agent),
            WebUiModule.AgentRiskChecksPath(agent),
            WebUiModule.StopAgentPath(agent),
        })
        {
            // A page that does not change by itself has nothing to pause, also when it is asked to.
            Assert.DoesNotContain("live-pause", await ada.Browser.PageAsync(path));
            Assert.DoesNotContain("live-pause", await ada.Browser.PageAsync($"{path}?live=paused"));
        }
    }

    [Fact]
    public async Task A_page_that_is_paused_carries_no_part_for_a_connection_says_when_it_was_written_and_offers_to_resume()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);
        var cycle = await application.GiveCycleAsync(ada.User.Id, agent, Feeding.Morning);

        // The two pages that show prices, which are read for a page that follows and for no other.
        string[] withPrices = [WebUiModule.DashboardPath, WebUiModule.AgentStatePath(agent)];

        foreach (var path in Following(agent, cycle))
        {
            var page = await ada.Browser.PageAsync($"{path}?live=paused");

            // No part that the page would make a connection for: nothing is followed for it, so
            // it changes no more by itself.
            Assert.Empty(ConnectedPart().Matches(page));

            // It says since when it is as it is, and leads back to the page that follows.
            var said = LivePages.PausedAt(Feeding.Morning, PageTimes.Utc) + (withPrices.Contains(path) ? $" {LivePages.PausedPrices}" : string.Empty);

            Assert.Equal((said, path, LivePages.Resume), Assert.Single(PauseOn(page)));
            Assert.Contains("24 November 2026, 09:00:00 UTC", said);
        }
    }

    [Fact]
    public async Task A_page_that_is_paused_is_written_with_what_its_parts_showed_and_follows_nothing()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning, state: AgentState.Running);

        await application.GiveTradeAsync(ada.User.Id, agent, Feeding.Morning, holdingSeconds: 600, closed: false);
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Feeding.Morning.AddSeconds(7)));
        feeding.Progress.Runs(ada.User.Id, agent, Feeding.Cycle(CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts));

        var page = AgentBrowser.Read(await ada.Browser.PageAsync($"{WebUiModule.AgentStatePath(agent)}?live=paused"));

        // The parts of the page are there, written once, with what they showed as it was
        // written: the cycle that runs with its team, the position, and the price that the
        // watch had. They are marked, so nothing in them moves for as long as a state lasts.
        Assert.Empty(ConnectedPart().Matches(page));
        Assert.Contains("<div class=\"live-parts\" data-live=\"paused\">", page);
        Assert.Contains("<h2>A cycle is running</h2>", page);
        Assert.Contains("aria-labelledby=\"team-heading\"", page);
        Assert.Contains("<h2>Position</h2>", page);
        Assert.Contains("<p class=\"live-price\">BTCUSDT last traded at 100250 USDT, as of 24 November 2026, 09:00:07 UTC.</p>", Plain(page));

        // Writing it followed no price, and the page that follows is not marked.
        Assert.Empty(feeding.Prices.Followed);
        Assert.DoesNotContain("data-live=", await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));

        // A page that shows no part writes nothing in their place.
        Assert.DoesNotContain("data-live=", await ada.Browser.PageAsync($"{WebUiModule.AgentsPath}?live=paused"));
    }

    [Fact]
    public async Task Pausing_stays_beside_the_other_choices_of_an_address_and_through_the_pages_of_a_list()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);

        // More than a page of notifications holds.
        for (var told = 0; told < 26; told++)
        {
            await application.GiveNotificationAsync(ada.User.Id, agent, Feeding.Morning.AddMinutes(-told));
        }

        // The second page, following: it is paused as the second page.
        var second = await ada.Browser.PageAsync($"{WebUiModule.NotificationsPath}?page=2");

        Assert.Equal($"{WebUiModule.NotificationsPath}?page=2&amp;live=paused", Assert.Single(PauseOn(second)).Address);
        Assert.Contains($"<a href=\"{WebUiModule.NotificationsPath}\">Newer notifications</a>", second);

        // Paused, it leads to the other pages of the list paused, and back to itself following.
        var paused = await ada.Browser.PageAsync($"{WebUiModule.NotificationsPath}?page=2&live=paused");

        Assert.Equal($"{WebUiModule.NotificationsPath}?page=2", Assert.Single(PauseOn(paused)).Address);
        Assert.Contains($"<a href=\"{WebUiModule.NotificationsPath}?live=paused\">Newer notifications</a>", paused);

        var first = await ada.Browser.PageAsync($"{WebUiModule.NotificationsPath}?live=paused");

        Assert.Contains($"<a href=\"{WebUiModule.NotificationsPath}?page=2&amp;live=paused\">Older notifications</a>", first);

        // The choice stands last, whatever order the address came in, and is said once.
        var turned = await ada.Browser.PageAsync($"{WebUiModule.NotificationsPath}?live=paused&page=2");

        Assert.Equal($"{WebUiModule.NotificationsPath}?page=2", Assert.Single(PauseOn(turned)).Address);

        // A link to another screen carries none: the bar leads to pages that follow.
        Assert.DoesNotContain($"{WebUiModule.DashboardPath}?live", paused);
    }

    [Fact]
    public async Task A_form_of_a_page_that_is_paused_leads_back_to_the_page_paused()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);

        // What its user does on a page that they paused does not have it follow behind their
        // back: marking a notification as read leads back to the page as it was, paused.
        foreach (var path in new[] { WebUiModule.NotificationsPath, WebUiModule.DashboardPath, WebUiModule.AgentStatePath(agent) })
        {
            var told = await application.GiveNotificationAsync(ada.User.Id, agent, Feeding.Morning);
            var paused = $"{path}?live=paused";
            var token = Browser.AntiforgeryTokenIn(await ada.Browser.PageAsync(paused));

            using var answer = await ada.Browser.SendFormAsync(paused, NotificationPages.MarkReadForm(told), token);
            var page = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal(paused, answer.RequestMessage!.RequestUri!.PathAndQuery);
            Assert.Equal(LivePages.Resume, Assert.Single(PauseOn(page)).Link);
            Assert.Empty(ConnectedPart().Matches(page));
        }

        // The same form of a page that follows leads back to the page that follows.
        var last = await application.GiveNotificationAsync(ada.User.Id, agent, Feeding.Morning);
        var following = Browser.AntiforgeryTokenIn(await ada.Browser.PageAsync(WebUiModule.NotificationsPath));

        using var back = await ada.Browser.SendFormAsync(WebUiModule.NotificationsPath, NotificationPages.MarkReadForm(last), following);

        Assert.Equal(WebUiModule.NotificationsPath, back.RequestMessage!.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData("live=on")]
    [InlineData("live=PAUSED")]
    [InlineData("live=paused&live=paused")]
    [InlineData("live=paused&live=")]
    public async Task An_address_that_names_a_choice_of_following_which_there_is_not_is_not_found(string choice)
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Feeding.Morning);

        // As an address with a page number that is none: it has no page.
        foreach (var path in new[] { WebUiModule.DashboardPath, WebUiModule.AgentsPath, WebUiModule.AgentStatePath(agent), WebUiModule.NotificationsPath })
        {
            using var answer = await ada.Browser.GetAsync($"{path}?{choice}", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        }

        // An address that makes no choice, or an empty one, is of the page that follows.
        var page = await ada.Browser.PageAsync($"{WebUiModule.DashboardPath}?live=");

        Assert.Equal(LivePages.Pause, Assert.Single(PauseOn(page)).Link);
        Assert.Single(ConnectedPart().Matches(page));
    }

    /// <summary>What a page says of whether it follows: what it says, where its link leads as the page writes it, and what the link says.</summary>
    private static IReadOnlyList<(string Says, string Address, string Link)> PauseOn(string page) =>
    [
        .. Pause().Matches(page).Select(pause => (
            WebUtility.HtmlDecode(pause.Groups[1].Value), pause.Groups[2].Value, WebUtility.HtmlDecode(pause.Groups[3].Value))),
    ];

    /// <summary>The pages that follow what changes, for one agent and one cycle of it.</summary>
    private static string[] Following(Guid agentId, Guid cycleId) =>
    [
        WebUiModule.DashboardPath,
        WebUiModule.AgentsPath,
        WebUiModule.AgentStatePath(agentId),
        WebUiModule.AgentCyclesPath(agentId),
        WebUiModule.AgentCyclePath(agentId, cycleId),
        WebUiModule.AgentTestCyclePath(agentId),
        WebUiModule.NotificationsPath,
    ];

    /// <summary>
    /// Writes the part that says the cycles of an agent again and again, as the connection of
    /// a page does, and keeps which of its parts were written and which were taken away.
    /// </summary>
#pragma warning disable BL0006 // What a renderer is handed is the framework's to shape: read here is which parts it names, and nothing else.
    private sealed class Written(IServiceProvider services, ILoggerFactory loggers) : Renderer(services, loggers)
    {
        private int? _part;

        /// <summary>The parts that were written so far, by the number that the framework gave each.</summary>
        public SortedSet<int> Parts { get; } = [];

        /// <summary>The parts that were taken away again.</summary>
        public List<int> TakenAway { get; } = [];

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Writes the part with the cycles that run now, on the screen of an agent.</summary>
        public Task ShowAsync(IReadOnlyList<RunningCycle> cycles, IReadOnlyList<LiveRole> team) =>
            Dispatcher.InvokeAsync(() =>
            {
                _part ??= AssignRootComponentId(InstantiateComponent(typeof(LiveCycleSteps)));

                return RenderRootComponentAsync(
                    _part.Value,
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(LiveCycleSteps.Cycles)] = cycles,
                        [nameof(LiveCycleSteps.SaysRest)] = true,
                        [nameof(LiveCycleSteps.Team)] = team,
                    }));
            });

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            for (var at = 0; at < renderBatch.UpdatedComponents.Count; at++)
            {
                Parts.Add(renderBatch.UpdatedComponents.Array[at].ComponentId);
            }

            for (var at = 0; at < renderBatch.DisposedComponentIDs.Count; at++)
            {
                TakenAway.Add(renderBatch.DisposedComponentIDs.Array[at]);
            }

            return Task.CompletedTask;
        }
    }
#pragma warning restore BL0006

    /// <summary>Stands for a part of a page that fails as it is written.</summary>
    private sealed class Fails : ComponentBase
    {
        public const string Secret = "what the error says";

        protected override void OnInitialized() => throw new InvalidOperationException(Secret);
    }

    // Where the framework marks a part of a page that the page makes its connection for, with
    // the key that tells the part apart.
    [GeneratedRegex("""<!--Blazor:\{"type":"server"[^>]*-->""")]
    private static partial Regex ConnectedPart();

    [GeneratedRegex("<p class=\"live-pause\">\\s*<span role=\"status\">([^<]*)</span>\\s*<a href=\"([^\"]*)\">([^<]*)</a>\\s*</p>")]
    private static partial Regex Pause();

    [GeneratedRegex(""""formattedComponentKey":"([^"]*)"""")]
    private static partial Regex Key();

    [GeneratedRegex("<input[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Input();

    [GeneratedRegex("""<li class="cycle-step[^"]*">(.*?)</li>""")]
    private static partial Regex Step();

    // A part of a page that says a cycle of an agent, or its team at rest, with what is in it.
    [GeneratedRegex("""<section class="live-cycle" aria-live="polite">(.*?)</section>""", RegexOptions.Singleline)]
    private static partial Regex LivePart();

    [GeneratedRegex(@">\s+<")]
    private static partial Regex Between();

    /// <summary>A page with nothing between one element and the next, for what is asked of the order of its elements.</summary>
    private static string Flat(string page) => Between().Replace(page, "><");

    /// <summary>A page without the elements that set a figure apart in its sentence (FR-ANI-2), for what is asked of the sentence.</summary>
    private static string Plain(string page) => Figure().Replace(page, string.Empty);

    [GeneratedRegex("<data class=\"figure\" value=\"[^\"]*\">|</data>")]
    private static partial Regex Figure();
}
