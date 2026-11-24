using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Experience;
using Ecnanib.Tests.LiveUpdates;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Live;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// The agent screen shows: live team status, the current position and protective orders, an
/// equity curve, the trade list, performance figures (return, win rate, average R, profit
/// factor, largest drawdown, fees, funding), and the decision timeline with every role's output
/// and the Risk Guard result (FR-RPT-2). A Demo or Live badge is always visible (FR-UI-3), and
/// Demo and Live are never combined (FR-BIN-11). The screen is the page of the state of the
/// agent. See docs/agent-screen.md.
/// </summary>
/// <remarks>
/// An agent of these tests has a capital of 1000 USDT. The orders that a test gives it are of a
/// position of 0.01 that was entered at 100000 and that its stop-loss closed at 98990: it lost
/// 10.7 in all, which is -1.07 R, and its equity was recorded as 989.3 a minute after. A trade
/// that a test gives it has a result and no orders: 10 of the asset for each R. The position
/// that a test leaves open holds 0.01 that was entered at 100000. The clock stands at nine in
/// the morning of 24 November 2026, UTC, and the prices are in the hands of the test: the page
/// asks Binance for none.
/// </remarks>
public partial class AgentScreenPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string TradesCaption = "The closed trades of the agent";

    private static readonly DateTimeOffset Morning = Feeding.Morning;

    private static readonly LivePrice Bitcoin = new(Market.Spot, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.LastTraded);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_screen_of_an_agent_shows_its_team_its_position_its_figures_its_equity_curve_its_trades_and_its_decisions()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-3), name: "Runner", state: AgentState.Running);

        // What its team decided: a plan that was approved three hours ago, whose orders are of
        // the position that its stop-loss closed, and one that was vetoed two hours ago. And a
        // cycle that its user ran as a test in between, which is no decision.
        var approved = await application.GiveCycleAsync(ada.User.Id, agent, Morning.AddHours(-3), CycleOutcome.Approved);
        var vetoed = await application.GiveCycleAsync(ada.User.Id, agent, Morning.AddHours(-2), CycleOutcome.Vetoed, orders: false);
        var test = await application.GiveCycleAsync(
            ada.User.Id, agent, Morning.AddMinutes(-150), CycleOutcome.Approved, orders: false, kind: CycleKind.Test);

        // A trade that made 2 R an hour ago, and the position that it holds now, with its stop-loss on the exchange.
        await application.GiveTradeAsync(ada.User.Id, agent, Morning.AddHours(-1), resultInR: 2m);

        var held = await application.GiveTradeAsync(ada.User.Id, agent, Morning, holdingSeconds: 600, closed: false);
        await application.GiveProtectionAsync(ada.User.Id, agent, held, OrderPurpose.StopLoss, 99_000m, Morning);
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning.AddSeconds(-3)));

        // Its equity as it was recorded: 1000, up to 1040, the 989.3 after its loss, and 1009.3.
        await application.GiveEquityAsync(
            ada.User.Id, agent, (Morning.AddDays(-2), 1000m), (Morning.AddDays(-1), 1040m), (Morning.AddHours(-1), 1009.3m));

        var markup = await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent));
        var page = AgentBrowser.Read(markup);

        Assert.Contains("<h1>State of the agent Runner</h1>", page);

        // The badge of its mode, under the heading (FR-UI-3).
        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", page);

        // Live team status: the state of the agent, the four roles with the model of each, and
        // what the team does now, which is written over the connection of the page as it changes.
        Assert.Contains("<p>State: <strong>Running</strong>, since 21 November 2026, 09:00 UTC.", page);
        Assert.Equal(
            [
                ("Market Analyst", $"{StoredAgentRows.TeamModel}, of Claude"),
                ("Sentiment Analyst", $"{StoredAgentRows.TeamModel}, of Claude"),
                ("Strategist", $"{StoredAgentRows.TeamModel}, of Claude"),
                ("Risk Manager", $"{StoredAgentRows.TeamModel}, of Claude"),
            ],
            Named().Matches(Roster().Match(page).Groups[1].Value).Select(role => (role.Groups[1].Value.Trim(), role.Groups[2].Value.Trim())));
        Assert.Contains("<p class=\"live-rest\">The team is at rest: no cycle of the agent runs at the moment.</p>", page);

        // The current position and its protective orders, as they rest on the exchange.
        Assert.Contains("<p>A position of 0.01 in BTCUSDT, entered at 100000 USDT, open since 24 November 2026, 08:50 UTC.</p>", page);
        Assert.Contains("<p>Its stop-loss rests on the exchange at 99000. No take-profit rests there for it.</p>", page);
        Assert.Contains("<p class=\"live-unrealized\">At that price the position would make 2.5 USDT, before fees and funding.</p>", page);

        // The performance figures, in Demo: 1000 less the 10.7 that its orders settled, with
        // the 2.5 that its position would make at 100250. The trades are -1.07 R and 2 R. The
        // deepest fall of its recorded equity is from 1040 to 989.3.
        Assert.Equal(
            [
                ("Equity", "991.8 USDT"),
                ("Net result", "-8.2 USDT"),
                ("Return", "-0.82 percent"),
                ("Win rate", "50 percent"),
                ("Average R", "+0.47 R"),
                ("Profit factor", "1.87"),
                ("Largest drawdown", "4.88 percent"),
                ("Fees", "0.5 USDT"),
                ("Funding", "-0.1 USDT"),
            ],
            FiguresOf(page));
        Assert.Contains("What the agent has made and lost in Demo, to date. Nothing of what it did in Live is in these figures.", page);
        Assert.Contains(
            "The open position is valued at the price as of 24 November 2026, 08:59:57 UTC. The return is the net result in percent of " +
            "the 1000 USDT that the agent is allocated now.",
            page);
        Assert.Contains(
            "The win rate, the average R, and the profit factor are of 2 closed trades in Demo, in R: the amount that was risked on a trade. Small sample.",
            page);
        Assert.Contains(
            "It was a fall of 50.7 USDT from its peak of 23 November 2026, 09:00 UTC, to 24 November 2026, 06:31 UTC.", page);

        // The equity curve, drawn on the server: a line that holds each recorded value until the
        // next, over the line of the capital. It is said in words as well.
        Assert.Contains("<h2 id=\"equity-heading\">Equity curve</h2>", page);
        Assert.Contains(
            "<svg class=\"equity-chart\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 600 200\" preserveAspectRatio=\"none\" role=\"img\" " +
            "aria-label=\"The equity of the agent over time\" aria-describedby=\"equity-caption\">",
            page);
        Assert.Equal("6,154.32 306.26,154.32 306.26,6 575.58,6 575.58,194 594,194 594,119.84", Line().Match(page).Groups[1].Value);
        Assert.Contains("<line class=\"equity-capital\" x1=\"0\" y1=\"154.32\" x2=\"600\" y2=\"154.32\"></line>", page);
        Assert.Contains(
            "<figcaption id=\"equity-caption\">Recorded 4 times, from 1000 USDT at 22 November 2026, 09:00 UTC to 1009.3 USDT at 24 November 2026, " +
            "08:00 UTC. Lowest: 989.3 USDT at 24 November 2026, 06:31 UTC. Highest: 1040 USDT at 23 November 2026, 09:00 UTC. The line holds each " +
            "value until the next, and the dashed line is the capital that the agent is allocated.</figcaption>",
            page);

        // The trade list, the latest close first. The open position is no trade yet.
        Assert.Contains("<p>2 closed trades, the latest close first, each with the mode that the agent was in.</p>", page);
        Assert.Equal(
            [
                "Closed", "Mode", "Symbol", "Market", "Side", "Quantity", "Entry price", "Exit price", "How it closed", "Net result", "In R", "Fees",
                "Funding", "Opened", "Held",
            ],
            ExperienceBrowser.ColumnsOf(page, TradesCaption));
        Assert.Equal(
            [
                [
                    "24 November 2026, 08:00 UTC", "Demo", "BTCUSDT", "Spot", "Long", "Not known", "100000", "Not known", "Take-profit", "+20 USDT",
                    "+2 R", "0 USDT", "0 USDT", "24 November 2026, 07:00 UTC", "1 hour",
                ],
                [
                    "24 November 2026, 06:30 UTC", "Demo", "BTCUSDT", "Spot", "Long", "0.01", "100000", "98990", "Stop-loss", "-10.7 USDT",
                    "-1.07 R", "0.5 USDT", "-0.1 USDT", "24 November 2026, 06:00 UTC", "30 minutes",
                ],
            ],
            ExperienceBrowser.RowsOf(page, TradesCaption));

        // The decision timeline: the latest decisions, the latest first. The test cycle is not among them.
        var decisions = Decision().Matches(page).Select(decision => decision.Groups[1].Value).ToList();

        Assert.Equal(
            ["Candle close at 24 November 2026, 07:00 UTC", "Candle close at 24 November 2026, 06:00 UTC"],
            decisions.Select(decision => Regex.Match(decision, "<h3[^>]*>([^<]*)</h3>").Groups[1].Value));
        Assert.Equal(["Vetoed", "Approved"], decisions.Select(decision => Regex.Match(decision, "<strong class=\"cycle-outcome cycle-[a-z]+\">([^<]*)</strong>").Groups[1].Value));
        Assert.DoesNotContain(test.ToString(), page);

        foreach (var (decision, cycle) in decisions.Zip([vetoed, approved]))
        {
            // It says its mode as a word: the page has the one badge of its agent.
            Assert.Matches(@"<p>\s*In Demo\. Outcome:", decision);
            Assert.DoesNotContain("mode-badge", decision);

            // Every role's output: each call with its model, how it ended, and what it answered.
            Assert.Equal(
                ["Market Analyst", "Sentiment Analyst", "Strategist", "Risk Manager"],
                Regex.Matches(decision, "<h4>([^<]*)</h4>").Select(title => title.Groups[1].Value).Take(4));
            Assert.Equal(4, Regex.Count(decision, $"Model: {StoredAgentRows.TeamModel}, of Claude\\."));
            Assert.Equal(4, Regex.Count(decision, "<strong class=\"call-outcome\">Valid result</strong>"));
            Assert.Equal(4, Regex.Count(decision, "<dl class=\"role-result\">"));
            Assert.Contains("<dt>Bias</dt><dd>bullish</dd>", decision);
            Assert.Contains("<dt>Action</dt><dd>open long</dd>", decision);
            Assert.Contains("<dt>Verdict</dt><dd>approve</dd>", decision);

            // The Risk Guard result: its verdict, and the outcome of every rule.
            Assert.Matches("<h4 id=\"decision-[0-9a-f]{32}-check-1-heading\">Risk Guard: Open a long position</h4>", decision);
            Assert.Contains("<strong class=\"guard-verdict\">Accepted</strong>", decision);
            Assert.Equal(Enum.GetValues<GuardRule>().Length, Regex.Count(decision, "<span class=\"rule-outcome rule-[a-z]+\">"));

            // And it leads to the timeline of its cycle, which has the rest of its record.
            Assert.Contains($"<a href=\"/agents/{agent}/cycles/{cycle}\">The whole record of this cycle, as a timeline</a>", decision);
        }

        // The orders that a decision led to, each as it is now, or why it placed none.
        Assert.Contains("<p class=\"no-order\">", decisions[0]);
        Assert.Equal(
            ["Entry order", "Stop-loss order"],
            Regex.Matches(decisions[1], "<li>\\s*<strong>([^<]*)</strong>").Select(order => order.Groups[1].Value));
        Assert.Equal(2, Regex.Count(decisions[1], "<span class=\"order-status\">Executed</span>, as it is now\\."));
        Assert.Contains($"<a href=\"/agents/{agent}/cycles\">Cycles of the agent</a>", page);

        // What the page was before is on it still: the controls of the agent, and the changes of its state.
        Assert.Contains("<button type=\"submit\">Pause the agent</button>", page);
        Assert.Contains("<caption>State changes</caption>", page);

        // Nothing of it carries a style of its own, and nothing of it handles an event: the
        // curve is drawn on the server, and the page is shown under the content security
        // policy as it is (NFR-SEC-4).
        Assert.DoesNotContain("style=", markup);
        Assert.DoesNotContain("<style", markup);
        Assert.DoesNotMatch(@"\son[a-z]+=", markup);
    }

    [Fact]
    public async Task An_agent_that_has_done_nothing_says_so_in_every_part_of_its_screen()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "New", capital: 250.5m);
        var teamless = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "Teamless", symbol: "ETHUSDT", team: false);

        var page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));

        // Its capital is its equity: nothing made, nothing lost, of no trade, for no fee and no funding.
        Assert.Equal(
            [
                ("Equity", "250.5 USDT"),
                ("Net result", "0 USDT"),
                ("Return", "0 percent"),
                ("Win rate", "Not available"),
                ("Average R", "Not available"),
                ("Profit factor", "Not available"),
                ("Largest drawdown", "None"),
                ("Fees", "0 USDT"),
                ("Funding", "0 USDT"),
            ],
            FiguresOf(page));
        Assert.Contains("The agent holds no position, so no price is in these figures.", page);
        Assert.Contains("The win rate, the average R, and the profit factor are of the closed trades of the agent in Demo. It has closed none yet.", page);
        Assert.Contains("The recorded equity has not fallen from a peak.", page);

        // No equity was recorded, so no curve is drawn. It has no trade, and its team has decided nothing.
        Assert.Contains("<p>The equity of the agent was not recorded in Demo yet. It is recorded once the agent runs.</p>", page);
        Assert.DoesNotContain("<svg", page);
        Assert.Contains("<p>The agent has closed no position yet.</p>", page);
        Assert.False(ExperienceBrowser.HasTable(page, TradesCaption));
        Assert.Contains("<p>The team has decided nothing yet: no cycle of the agent was recorded.", page);
        Assert.Empty(Decision().Matches(page));

        // It holds nothing, and its team is at rest.
        Assert.Contains("<p>The agent holds no position.</p>", page);
        Assert.Contains("The team is at rest", page);

        // An equity that was recorded once is said, and no line is drawn of it yet.
        await application.GiveEquityAsync(ada.User.Id, agent, (Morning.AddHours(-1), 250.5m));

        var once = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));

        Assert.Contains(
            "<figcaption id=\"equity-caption\">Recorded once: 250.5 USDT at 24 November 2026, 08:00 UTC. A line is drawn once it is recorded again.</figcaption>",
            once);
        Assert.DoesNotContain("<svg", once);

        // A role that was given no model says so, in words of its own.
        var withoutTeam = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(teamless)));

        Assert.Equal(
            ["No model chosen", "No model chosen", "No model chosen", "No model chosen"],
            Named().Matches(Roster().Match(withoutTeam).Groups[1].Value).Select(role => role.Groups[2].Value.Trim()));

        // A form of the page that changes nothing, such as starting an agent that has no team,
        // is answered on the page with why, and the page still shows how the agent has performed.
        var (path, refused) = await ada.Browser.StartAgentAsync(teamless);

        Assert.Equal(WebUiModule.AgentStatePath(teamless), path);
        Assert.Contains("role=\"alert\"", refused);
        Assert.Equal(9, FiguresOf(AgentBrowser.Read(refused)).Count);
    }

    [Fact]
    public async Task A_figure_that_is_not_known_is_said_as_not_known_and_never_as_a_number()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "Holds", state: AgentState.Running);

        await application.GiveOrdersAsync(ada.User.Id, agent, cycleId: null, Morning.AddHours(-2));
        await application.GiveTradeAsync(ada.User.Id, agent, Morning, holdingSeconds: 600, closed: false);

        // Nobody follows the price of its position, and the page asks Binance for none: its
        // equity, its net result, and its return are not known. What is settled is said all the same.
        var unknown = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));
        var figures = FiguresOf(unknown).ToDictionary(figure => figure.Name, figure => figure.Value);

        Assert.Equal(
            ("Not known at the moment", "Not known at the moment", "Not known at the moment"),
            (figures["Equity"], figures["Net result"], figures["Return"]));
        Assert.Equal(("0 percent", "-1.07 R", "0.5 USDT", "-0.1 USDT"), (figures["Win rate"], figures["Average R"], figures["Fees"], figures["Funding"]));
        Assert.Contains(
            "The open position cannot be valued right now: the price of BTCUSDT is not known at the moment. The page asks for it, and shows the " +
            "figures once it is read.",
            unknown);

        // The equity as it is settled is not written in its place.
        Assert.DoesNotContain("<dd>989.3 USDT</dd>", unknown);

        // The watch has read the price by now, for the page that follows it: 2.5 more.
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning.AddSeconds(4)));

        var valued = FiguresOf(AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)))).ToDictionary(figure => figure.Name, figure => figure.Value);

        Assert.Equal(("991.8 USDT", "-8.2 USDT", "-0.82 percent"), (valued["Equity"], valued["Net result"], valued["Return"]));
    }

    [Fact]
    public async Task The_figures_and_the_curve_are_of_the_mode_of_the_agent_and_what_it_did_in_the_other_mode_says_its_mode_in_words()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-3), name: "Switched", state: AgentState.Stopped);

        // What it did in Demo: a decision whose orders lost 10.7, with its equity recorded after.
        await application.GiveCycleAsync(ada.User.Id, agent, Morning.AddHours(-3));

        var inDemo = FiguresOf(AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent))))
            .ToDictionary(figure => figure.Name, figure => figure.Value);

        Assert.Equal(("989.3 USDT", "-1.07 percent", "-1.07 R", "1.07 percent"), (inDemo["Equity"], inDemo["Return"], inDemo["Average R"], inDemo["Largest drawdown"]));

        // Its user switches it to Live (FR-BIN-11).
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await context.Set<Agent>()
                .Where(stored => stored.Id == agent)
                .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.Mode, TradingMode.Live), Cancellation);
        }

        var page = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(agent)));

        // The badge of Live, and figures of Live alone: nothing of what it did in Demo is in them.
        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Live))} ", page);
        Assert.Contains("What the agent has made and lost in Live, to date. Nothing of what it did in Demo is in these figures.", page);
        Assert.Equal(
            [
                ("Equity", "1000 USDT"),
                ("Net result", "0 USDT"),
                ("Return", "0 percent"),
                ("Win rate", "Not available"),
                ("Average R", "Not available"),
                ("Profit factor", "Not available"),
                ("Largest drawdown", "None"),
                ("Fees", "0 USDT"),
                ("Funding", "0 USDT"),
            ],
            FiguresOf(page));
        Assert.Contains("<p>The equity of the agent was not recorded in Live yet. It is recorded once the agent runs.</p>", page);
        Assert.DoesNotContain("<svg", page);
        Assert.DoesNotContain("989.3", page);

        // The trade and the decision of Demo are listed, and each says Demo, as a word: neither
        // part of the page carries a badge.
        var trades = Part().Matches(page).Select(part => part.Value).Single(part => part.Contains("agent-trades", StringComparison.Ordinal));
        var decisions = Part().Matches(page).Select(part => part.Value).Single(part => part.Contains("agent-decisions", StringComparison.Ordinal));

        Assert.Equal("Demo", Assert.Single(ExperienceBrowser.RowsOf(page, TradesCaption))[1]);
        Assert.Matches(@"<p>\s*In Demo\. Outcome:", decisions);
        Assert.DoesNotContain("mode-badge", trades);
        Assert.DoesNotContain("mode-badge", decisions);
    }

    [Fact]
    public async Task The_trades_are_listed_a_page_at_a_time_and_a_page_that_there_is_not_is_not_found()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "Busy");
        var state = WebUiModule.AgentStatePath(agent);

        // 27 trades, a minute apart, the last one a minute ago.
        await application.GiveTradesAsync(ada.User.Id, agent, count: 27, latest: Morning.AddMinutes(-1), roles: []);

        var first = AgentBrowser.Read(await ada.Browser.PageAsync(state));

        // The latest 25, and the way to the older ones. The figures are of every trade.
        Assert.Contains("<p>27 closed trades, the latest close first, each with the mode that the agent was in. Page 1 of 2.</p>", first);
        Assert.Equal(25, ExperienceBrowser.RowsOf(first, TradesCaption).Count);
        Assert.Equal("24 November 2026, 08:59 UTC", ExperienceBrowser.RowsOf(first, TradesCaption)[0][0]);
        Assert.Contains($"<a href=\"{state}?trades=2\">Older trades</a>", first);
        Assert.DoesNotContain("Newer trades", first);
        Assert.Contains("of 27 closed trades in Demo", first);

        var second = AgentBrowser.Read(await ada.Browser.PageAsync($"{state}?trades=2"));

        Assert.Contains("Page 2 of 2.", second);
        Assert.Equal(
            ["24 November 2026, 08:34 UTC", "24 November 2026, 08:33 UTC"],
            ExperienceBrowser.RowsOf(second, TradesCaption).Select(row => row[0]));
        Assert.Contains($"<a href=\"{state}\">Newer trades</a>", second);
        Assert.DoesNotContain("Older trades", second);
        Assert.Contains("of 27 closed trades in Demo", second);

        // The page has links, and no field: it asks for itself again when something changes,
        // and what its user had typed would be gone.
        Assert.All(Input().Matches(second), input => Assert.Contains("type=\"hidden\"", input.Value, StringComparison.Ordinal));
        Assert.DoesNotContain("<select", second, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<textarea", second, StringComparison.OrdinalIgnoreCase);

        // The first page is there by its number as well.
        Assert.Contains("Page 1 of 2.", await ada.Browser.PageAsync($"{state}?trades=1"));

        // An address that names a page which there is not is answered with "not found": the
        // page shows what its address asks for, and never something else in its place.
        foreach (var choice in new[] { "trades=3", "trades=0", "trades=-1", "trades=01", "trades=two", "trades=1&trades=2" })
        {
            using var response = await ada.Browser.GetAsync($"{state}?{choice}", Cancellation);

            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"The page of {choice} was answered with {response.StatusCode}.");
        }
    }

    [Fact]
    public async Task The_screen_of_an_agent_of_another_user_is_not_found_and_reading_a_screen_writes_nothing()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);
        var hers = await application.GiveAgentAsync(grace.User.Id, Morning.AddDays(-3), name: "Scalp of Grace", state: AgentState.Running, capital: 777.5m);
        var mine = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "Swing of Ada", symbol: "SOLUSDT");

        var cycle = await application.GiveCycleAsync(grace.User.Id, hers, Morning.AddHours(-3));
        await application.GiveTradeAsync(grace.User.Id, hers, Morning, closed: false);
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning));

        var before = application.RowsOf(grace.User.Id);
        var adasBefore = application.RowsOf(ada.User.Id);

        // The page of an agent of another user is not found, like that of an agent that does
        // not exist, whatever page of its trades is asked for, and says nothing of the agent (NFR-SEC-3).
        foreach (var path in new[] { WebUiModule.AgentStatePath(hers), WebUiModule.AgentStatePath(hers, 2), WebUiModule.AgentStatePath(Guid.NewGuid()) })
        {
            using var response = await ada.Browser.GetAsync(path, Cancellation);
            var said = await response.Content.ReadAsStringAsync(Cancellation);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("of Grace", said);
            Assert.DoesNotContain("769.3", said);
            Assert.DoesNotContain("Performance", said);
            Assert.DoesNotContain(cycle.ToString(), said);
        }

        // Ada reads her own, which holds nothing of Grace's.
        var adas = AgentBrowser.Read(await ada.Browser.PageAsync(WebUiModule.AgentStatePath(mine)));

        Assert.Equal(("Equity", "1000 USDT"), FiguresOf(adas)[0]);
        Assert.DoesNotContain("of Grace", adas);
        Assert.DoesNotContain(hers.ToString(), adas);

        // And Grace reads hers: 777.5 less the 10.7, with the 2.5 that her position would make.
        var graces = AgentBrowser.Read(await grace.Browser.PageAsync(WebUiModule.AgentStatePath(hers)));

        Assert.Equal(("Equity", "769.3 USDT"), FiguresOf(graces)[0]);
        Assert.Single(ExperienceBrowser.RowsOf(graces, TradesCaption));
        Assert.Single(Decision().Matches(graces));

        // Reading a screen writes nothing: no equity is recorded for asking.
        Assert.Equal(before, application.RowsOf(grace.User.Id));
        Assert.Equal(adasBefore, application.RowsOf(ada.User.Id));

        // A visitor is sent to sign in, and is shown nothing of it.
        using var visitor = application.CreateClient();
        var (address, shown) = await visitor.OpenAsync(WebUiModule.AgentStatePath(hers));

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(WebUiModule.AgentStatePath(hers))}", address);
        Assert.DoesNotContain("of Grace", shown);
    }

    /// <summary>The performance figures of a page, in the order that they are on it.</summary>
    private static IReadOnlyList<(string Name, string Value)> FiguresOf(string page) =>
        [
            .. Named().Matches(Figures().Match(page).Groups[1].Value)
                .Select(named => (named.Groups[1].Value.Trim(), named.Groups[2].Value.Trim())),
        ];

    [GeneratedRegex("<dl class=\"dashboard-figures agent-figures\">(.*?)</dl>", RegexOptions.Singleline)]
    private static partial Regex Figures();

    [GeneratedRegex("<dl class=\"agent-roster\"[^>]*>(.*?)</dl>", RegexOptions.Singleline)]
    private static partial Regex Roster();

    [GeneratedRegex(@"<dt>(.*?)</dt>\s*<dd>(.*?)</dd>", RegexOptions.Singleline)]
    private static partial Regex Named();

    [GeneratedRegex("<polyline class=\"equity-line\" points=\"([^\"]*)\"")]
    private static partial Regex Line();

    [GeneratedRegex("<article class=\"decision\"[^>]*>(.*?)</article>", RegexOptions.Singleline)]
    private static partial Regex Decision();

    [GeneratedRegex("<section class=\"agent-[a-z]+\"[^>]*>.*?</section>", RegexOptions.Singleline)]
    private static partial Regex Part();

    [GeneratedRegex("<input[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Input();
}
