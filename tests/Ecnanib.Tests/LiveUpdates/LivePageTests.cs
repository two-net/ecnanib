using System.Net;
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
using Ecnanib.WebUi;
using Ecnanib.WebUi.Live;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.LiveUpdates;

/// <summary>
/// The pages that follow what changes (FR-NTF-2): each carries one part for which it makes its
/// connection, which is the one push channel from the application to the browser (NFR-ARC-6),
/// and is written with what that part shows then: the steps of a cycle that runs, and the
/// position of an agent with the price that it is valued at. See docs/live-updates.md.
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

        Assert.Contains("<p class=\"live-price\">BTCUSDT last traded at 100250 USDT, as of 24 November 2026, 09:00:07 UTC.</p>", page);
        Assert.Contains("<p class=\"live-unrealized\">At that price the position would make 2.5 USDT, before fees and funding.</p>", page);

        // A position of the Futures market says its side, and is valued at the mark price. What
        // it would make is said in words, and a loss as a loss.
        page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(shorts)));

        Assert.Contains("<p>A long position of 0.01 in BTCUSDT, entered at 100000 USDT, open since 24 November 2026, 08:50 UTC.</p>", page);
        Assert.Contains("<p class=\"live-price\">The mark price of BTCUSDT is 99400.5 USDT, as of 24 November 2026, 09:00:08 UTC.</p>", page);
        Assert.Contains("<p class=\"live-unrealized\">At that price the position would lose 5.995 USDT, before fees and funding.</p>", page);

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

        const string AtRest = "<section class=\"live-cycle\" aria-live=\"polite\"><p class=\"live-rest\">The team is at rest: no cycle of the agent runs at the moment.</p></section>";

        // The screen of the agent says what its team does now, also when it does nothing
        // (FR-RPT-2). The other pages that show the cycles which run have nothing to say then.
        Assert.Contains(AtRest, AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent))));

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
            $"<p class=\"live-stopped\" role=\"status\">{LivePages.Stopped(reference!)}</p>",
            written.Trim());
        Assert.DoesNotContain(Fails.Secret, written);
    }

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

    [GeneratedRegex(""""formattedComponentKey":"([^"]*)"""")]
    private static partial Regex Key();

    [GeneratedRegex("<input[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Input();

    [GeneratedRegex("""<li class="cycle-step[^"]*">(.*?)</li>""")]
    private static partial Regex Step();
}
