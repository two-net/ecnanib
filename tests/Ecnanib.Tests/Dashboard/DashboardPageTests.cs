using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Accounts;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Notifications;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.DecisionEngine;
using Ecnanib.Tests.Experience;
using Ecnanib.Tests.LiveUpdates;
using Ecnanib.Tests.Notifications;
using Ecnanib.WebUi.Live;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.Dashboard;

/// <summary>
/// The dashboard shows, per mode: total equity across agents, profit and loss for the day, open
/// positions, a status card per agent, recent decisions, and alerts (FR-RPT-1). Demo and Live
/// figures are shown separately and are never combined (FR-BIN-11). See docs/dashboard.md.
/// </summary>
/// <remarks>
/// An agent of these tests has a capital of 1000 USDT unless a test says otherwise. The orders
/// that a test gives one are of a position that its stop-loss closed, which lost 10.7 in all.
/// The position that a test leaves open holds 0.01 that was entered at 100000. The clock stands
/// at nine in the morning of 24 November 2026, UTC, and the prices are in the hands of the test:
/// the page asks Binance for none.
/// </remarks>
public partial class DashboardPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string Dashboard = "/dashboard";

    private static readonly DateTimeOffset Morning = Feeding.Morning;

    private static readonly LivePrice Bitcoin = new(Market.Spot, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.LastTraded);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_dashboard_shows_for_each_mode_its_equity_its_day_its_positions_its_agents_its_decisions_and_its_alerts()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);

        // In Demo: an agent that lost 10.7 today and holds a position, and a draft.
        var runner = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-3), name: "Demo runner", state: AgentState.Running);
        var draft = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-2), name: "Demo draft", symbol: "ETHUSDT");

        await application.GiveOrdersAsync(ada.User.Id, runner, cycleId: null, Morning.AddHours(-2));
        await application.GiveTradeAsync(ada.User.Id, runner, Morning, holdingSeconds: 600, closed: false);
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning.AddSeconds(-3)));

        // In Live: one agent with half the capital, which a daily loss limit halted.
        var live = await application.GiveAgentAsync(
            ada.User.Id,
            Morning.AddDays(-1),
            name: "Live swing",
            state: AgentState.Halted,
            symbol: "SOLUSDT",
            mode: TradingMode.Live,
            cause: AgentStateCause.DailyLossLimitReached,
            timeframe: Timeframe.FourHours,
            capital: 500m);

        // What their teams decided, and what the service told their user.
        var vetoed = await application.GiveCycleAsync(ada.User.Id, runner, Morning.AddHours(-1), CycleOutcome.Vetoed, orders: false);
        await application.GiveCycleAsync(ada.User.Id, draft, Morning.AddHours(-3), CycleOutcome.Hold, orders: false);
        await application.GiveCycleAsync(ada.User.Id, runner, Morning.AddHours(-2), CycleOutcome.Approved, orders: false, kind: CycleKind.Test);
        var approved = await application.GiveCycleAsync(ada.User.Id, live, Morning.AddHours(-4), CycleOutcome.Approved, orders: false);

        await application.GiveNotificationAsync(
            ada.User.Id, runner, Morning.AddMinutes(-30), "Something about Demo.", NotificationType.ProtectionFailure, TradingMode.Demo);
        await application.GiveNotificationAsync(
            ada.User.Id, live, Morning.AddMinutes(-20), "Something about Live.", NotificationType.AgentHalted, TradingMode.Live);
        await application.GiveNotificationAsync(
            ada.User.Id, agentId: null, Morning.AddMinutes(-10), "Something about a key of an AI provider.", NotificationType.KeyInvalid);

        var markup = await ada.Browser.PageAsync(Dashboard);
        var page = AgentBrowser.Read(markup);

        Assert.Contains("<title>Dashboard</title>", markup);
        Assert.Contains("<h1>Dashboard</h1>", markup);
        Assert.Contains("Demo and Live are shown apart, each under its own heading, and are never added up.", page);
        Assert.Contains("Profit and loss today is the equity now, less the equity as the UTC day began.", page);

        // Live first, where real money is at stake, and then Demo: each under its own heading,
        // with the badge of its mode.
        Assert.True(page.IndexOf("Your agents in Live", StringComparison.Ordinal) < page.IndexOf("Your agents in Demo", StringComparison.Ordinal));

        var ofLive = SectionOf(page, TradingMode.Live);
        var ofDemo = SectionOf(page, TradingMode.Demo);

        Assert.Matches($@"<h2 id=""dashboard-live-heading"">Your agents in Live</h2>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Live))} ", ofLive);
        Assert.Matches($@"<h2 id=""dashboard-demo-heading"">Your agents in Demo</h2>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", ofDemo);
        Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Demo), ofLive);
        Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Live), ofDemo);

        // Total equity and the day. Demo: 1000 less the 10.7, with the 2.5 that the position
        // would make at 100250, and the 1000 of the draft. Live: the capital of its one agent.
        Assert.Equal([("Total equity", "1991.8 USDT"), ("Profit and loss today", "-8.2 USDT")], FiguresOf(ofDemo));
        Assert.Equal([("Total equity", "500 USDT"), ("Profit and loss today", "0 USDT")], FiguresOf(ofLive));
        Assert.Contains("Of 2 agents, with 2000 USDT of allocated capital. Open positions are valued at prices as of 24 November 2026, 08:59:57 UTC.", ofDemo);
        Assert.Contains("Of 1 agent, with 500 USDT of allocated capital. No agent holds a position, so no price is in these figures.", ofLive);

        // The open positions, each with its protection as it rests, and what it would make now.
        Assert.Equal(
            [
                "Agent", "Symbol", "Market", "Side", "Quantity", "Entry price", "Stop-loss", "Take-profit", "Valued at", "Would make now",
                "Open since",
            ],
            ExperienceBrowser.ColumnsOf(page, "The open positions of your agents in Demo"));
        Assert.Equal(
            [
                [
                    "Demo runner", "BTCUSDT", "Spot", "Long", "0.01", "100000", "None rests", "None rests",
                    "100250, as of 24 November 2026, 08:59:57 UTC", "+2.5 USDT", "24 November 2026, 08:50 UTC",
                ],
            ],
            ExperienceBrowser.RowsOf(page, "The open positions of your agents in Demo"));
        Assert.Contains($"<a href=\"/agents/{runner}/state\">Demo runner</a>", ofDemo);
        Assert.Contains("<p>No agent in Live holds a position.</p>", ofLive);
        Assert.False(ExperienceBrowser.HasTable(page, "The open positions of your agents in Live"));

        // What moves is told what the page holds (FR-ANI-2, FR-ANI-3): a figure by the element
        // around its number, which says its value, an item of a list by its key, and the state
        // of an agent by its name. See docs/motion.md.
        Assert.Contains("<dd><data class=\"figure\" value=\"1991.8\">1991.8</data> USDT</dd>", ofDemo);
        Assert.Contains("<dd><data class=\"figure\" value=\"-8.2\">-8.2</data> USDT</dd>", ofDemo);
        Assert.Contains("<td><data class=\"figure\" value=\"100250\">100250</data>, as of 24 November 2026, 08:59:57 UTC</td>", ofDemo);
        Assert.Contains("<td><data class=\"figure\" value=\"2.5\">+2.5</data> USDT</td>", ofDemo);
        Assert.Contains("<dl class=\"dashboard-figures\" data-key=\"USDT\">", ofDemo);
        Assert.Contains($"<tr data-key=\"{runner}\">", ofDemo);
        Assert.Contains($"<li class=\"agent-card\" data-key=\"{runner}\">", ofDemo);
        Assert.Contains($"<li class=\"agent-card\" data-key=\"{live}\">", ofLive);
        Assert.Contains("<dd data-change=\"Running\">Running, since 21 November 2026, 09:00 UTC</dd>", ofDemo);
        Assert.Contains("<dd data-change=\"Halted\">", ofLive);
        Assert.Contains($"<tr data-key=\"{vetoed}\">", ofDemo);
        Assert.Contains($"<tr data-key=\"{approved}\">", ofLive);

        // A card for each agent, in the section of its mode: its state, its equity, its day,
        // and what it holds. One that is halted says why, and until when.
        Assert.Equal(
            [
                new Card("Demo runner", "BTCUSDT on Spot, 1H", "Running, since 21 November 2026, 09:00 UTC", "991.8 USDT", "-8.2 USDT", "Holds 0.01 in BTCUSDT, long"),
                new Card("Demo draft", "ETHUSDT on Spot, 1H", "Draft, since 22 November 2026, 09:00 UTC", "1000 USDT", "0 USDT", "Holds no position"),
            ],
            CardsOf(ofDemo));
        Assert.Equal(
            [new Card("Live swing", "SOLUSDT on Spot, 4H", "Halted, since 23 November 2026, 09:00 UTC", "500 USDT", "0 USDT", "Holds no position")],
            CardsOf(ofLive));
        Assert.Contains("The agent's loss in the UTC day reached its daily loss limit", ofLive);
        Assert.Contains("The agent is halted until 24 November 2026, 00:00 UTC", ofLive);

        // What each state of the cards means is said once, under the cards of both modes
        // (NFR-USA-2): the three states that the cards show, and none that they do not show.
        Assert.Contains("<h2>What the states mean</h2>", page);
        Assert.All(
            new[] { AgentState.Draft, AgentState.Running, AgentState.Halted },
            state =>
            {
                Assert.Contains($"<dt>{AgentPages.Describe(state)}</dt>", page);
                Assert.Contains($"<dd>{AgentPages.Explain(state)}</dd>", page);
            });
        Assert.All(
            new[] { AgentState.Paused, AgentState.AttentionRequired, AgentState.Stopped },
            state => Assert.DoesNotContain($"<dt>{AgentPages.Describe(state)}</dt>", page));
        Assert.DoesNotContain("You created the agent.", ofDemo);

        // The recent decisions, latest first, each leading to the timeline of its cycle. The
        // test cycle is not among them.
        Assert.Equal(["Candle close", "Agent", "Outcome"], ExperienceBrowser.ColumnsOf(page, "The recent decisions of your agents in Demo"));
        Assert.Equal(
            [
                ["24 November 2026, 08:00 UTC", "Demo runner", "Vetoed"],
                ["24 November 2026, 06:00 UTC", "Demo draft", "Hold"],
            ],
            ExperienceBrowser.RowsOf(page, "The recent decisions of your agents in Demo"));
        Assert.Equal(
            [["24 November 2026, 05:00 UTC", "Live swing", "Approved"]],
            ExperienceBrowser.RowsOf(page, "The recent decisions of your agents in Live"));
        Assert.Contains($"<a href=\"/agents/{runner}/cycles/{vetoed}\">24 November 2026, 08:00 UTC</a>", ofDemo);
        Assert.Contains($"<a href=\"/agents/{live}/cycles/{approved}\">24 November 2026, 05:00 UTC</a>", ofLive);

        // The alerts: what the service told its user and they have not read, by the mode that
        // each is about, each leading to its agent. What is about no mode is above both.
        Assert.Contains("<strong>Order or protection failure</strong>, 24 November 2026, 08:30 UTC. Something about Demo.", ofDemo);
        Assert.Contains($"<a href=\"/agents/{runner}/state\">State of Demo runner</a>", ofDemo);
        Assert.Contains("<strong>Agent halted</strong>, 24 November 2026, 08:40 UTC. Something about Live.", ofLive);
        Assert.Contains($"<a href=\"/agents/{live}/state\">State of Live swing</a>", ofLive);
        Assert.DoesNotContain("Something about Live.", ofDemo);
        Assert.DoesNotContain("Something about Demo.", ofLive);

        var ofAccount = Regex.Match(page, "<section class=\"dashboard-account\".*?</section>", RegexOptions.Singleline).Value;

        Assert.Contains("<h2 id=\"dashboard-account-alerts\">Alerts about your account</h2>", ofAccount);
        Assert.Contains("<strong>Key invalid</strong>, 24 November 2026, 08:50 UTC. Something about a key of an AI provider.", ofAccount);
        Assert.DoesNotContain("Something about a key", ofDemo + ofLive);
        Assert.True(page.IndexOf("dashboard-account-alerts", StringComparison.Ordinal) < page.IndexOf("dashboard-live-heading", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_user_who_has_no_agent_is_told_so_for_each_mode_and_is_shown_no_figure()
    {
        using var feeding = new Feeding();
        var ada = await feeding.SignedInAsync(Ada);

        var page = AgentBrowser.Read(await ada.Browser.PageAsync(Dashboard));

        foreach (var mode in Enum.GetValues<TradingMode>())
        {
            var section = SectionOf(page, mode);

            // No agent, so no total: nothing is written as a number that stands for nothing.
            Assert.Contains($"<p>You have no agent in {mode}.</p>", section);
            Assert.Empty(FiguresOf(section));
            Assert.Empty(CardsOf(section));
            Assert.DoesNotContain("USDT", section);
            Assert.Contains($"<p>No team has decided anything in {mode} yet.</p>", section);
            Assert.Contains($"<p>Nothing about {mode} waits to be read.</p>", section);
        }

        Assert.DoesNotContain("dashboard-account-alerts", page);
        Assert.DoesNotContain("<table", page);
    }

    [Fact]
    public async Task Demo_and_Live_are_never_added_up_also_for_an_agent_that_was_switched_from_one_to_the_other()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-3), name: "Switched", state: AgentState.Stopped);
        var stays = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-3), name: "Stays", symbol: "ETHUSDT", capital: 300m);

        // What it did in Demo: it lost 10.7, its team decided, and its user was told.
        await application.GiveOrdersAsync(ada.User.Id, agent, cycleId: null, Morning.AddHours(-2));
        await application.GiveCycleAsync(ada.User.Id, agent, Morning.AddHours(-2), CycleOutcome.Approved, orders: false);
        await application.GiveNotificationAsync(
            ada.User.Id, agent, Morning.AddHours(-1), "The position in BTCUSDT (Demo) was closed by its stop-loss.", NotificationType.PositionClosed, TradingMode.Demo);

        var before = AgentBrowser.Read(await ada.Browser.PageAsync(Dashboard));

        Assert.Equal([("Total equity", "1289.3 USDT"), ("Profit and loss today", "-10.7 USDT")], FiguresOf(SectionOf(before, TradingMode.Demo)));
        Assert.Empty(FiguresOf(SectionOf(before, TradingMode.Live)));

        // Its user switches it to Live, where it has done nothing yet.
        await using (var context = await application.Contexts.CreateDbContextAsync(Cancellation))
        {
            await context.Set<Agent>()
                .Where(stored => stored.Id == agent)
                .ExecuteUpdateAsync(row => row.SetProperty(stored => stored.Mode, TradingMode.Live), Cancellation);
        }

        var page = AgentBrowser.Read(await ada.Browser.PageAsync(Dashboard));
        var ofLive = SectionOf(page, TradingMode.Live);
        var ofDemo = SectionOf(page, TradingMode.Demo);

        // In Live it is worth its capital, and has made nothing: what it lost in Demo is no
        // loss of Live. Its card is in Live, and in Live alone.
        Assert.Equal([("Total equity", "1000 USDT"), ("Profit and loss today", "0 USDT")], FiguresOf(ofLive));
        Assert.Equal(["Switched"], CardsOf(ofLive).Select(card => card.Name));

        // Demo adds up the agent that is in Demo, and nothing of the one that left it.
        Assert.Equal([("Total equity", "300 USDT"), ("Profit and loss today", "0 USDT")], FiguresOf(ofDemo));
        Assert.Equal(["Stays"], CardsOf(ofDemo).Select(card => card.Name));

        // What its team decided in Demo, and what its user was told about Demo, stay with Demo:
        // each says the mode that it happened in, and not the mode that the agent is in now.
        Assert.Equal(
            [["24 November 2026, 07:00 UTC", "Switched", "Approved"]],
            ExperienceBrowser.RowsOf(page, "The recent decisions of your agents in Demo"));
        Assert.Contains("The position in BTCUSDT (Demo) was closed by its stop-loss.", ofDemo);
        Assert.Contains("<p>No team has decided anything in Live yet.</p>", ofLive);
        Assert.Contains("<p>Nothing about Live waits to be read.</p>", ofLive);
        Assert.DoesNotContain("(Demo)", ofLive);
        Assert.DoesNotContain("-10.7", page);
        Assert.DoesNotContain("1289.3", page);
        Assert.DoesNotContain("1300 USDT", page);
    }

    [Fact]
    public async Task What_is_not_known_is_said_as_not_known_and_never_as_a_number()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var holds = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-3), name: "Holds", state: AgentState.Running);

        await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-2), name: "Idle", symbol: "ETHUSDT");
        await application.GiveOrdersAsync(ada.User.Id, holds, cycleId: null, Morning.AddHours(-2));
        await application.GiveTradeAsync(ada.User.Id, holds, Morning, holdingSeconds: 600, closed: false);

        // Nobody follows the price of what the agent holds yet, and the page asks Binance for
        // none: what the position is worth is not known, and so is everything that it is part of.
        var unknown = SectionOf(AgentBrowser.Read(await ada.Browser.PageAsync(Dashboard)), TradingMode.Demo);

        Assert.Equal(
            [("Total equity", "Not known at the moment"), ("Profit and loss today", "Not known at the moment")], FiguresOf(unknown));
        Assert.Contains(
            "Of 2 agents, with 2000 USDT of allocated capital. An open position cannot be valued right now: the price of BTCUSDT is not known at the moment.",
            unknown);
        Assert.Equal(
            [
                new Card("Holds", "BTCUSDT on Spot, 1H", "Running, since 21 November 2026, 09:00 UTC", "Not known at the moment", "Not known at the moment", "Holds 0.01 in BTCUSDT, long"),
                new Card("Idle", "ETHUSDT on Spot, 1H", "Draft, since 22 November 2026, 09:00 UTC", "1000 USDT", "0 USDT", "Holds no position"),
            ],
            CardsOf(unknown));
        Assert.Equal(
            [
                [
                    "Holds", "BTCUSDT", "Spot", "Long", "0.01", "100000", "None rests", "None rests", "Not known at the moment",
                    "Not known at the moment", "24 November 2026, 08:50 UTC",
                ],
            ],
            ExperienceBrowser.RowsOf(unknown, "The open positions of your agents in Demo"));

        // Not what is settled of it, and not the sum of the agents that are known.
        Assert.DoesNotContain("989.3", unknown);
        Assert.DoesNotContain("1989.3", unknown);
        Assert.DoesNotContain("-10.7", unknown);

        // The price is read: a loss is written with its sign, and nothing relies on a colour.
        feeding.Prices.Say(Bitcoin, new WatchedPrice(99_400m, Morning.AddSeconds(-1)));

        var known = SectionOf(AgentBrowser.Read(await ada.Browser.PageAsync(Dashboard)), TradingMode.Demo);

        Assert.Equal([("Total equity", "1983.3 USDT"), ("Profit and loss today", "-16.7 USDT")], FiguresOf(known));
        Assert.Equal("-6 USDT", ExperienceBrowser.RowsOf(known, "The open positions of your agents in Demo")[0][9]);
        Assert.DoesNotContain("Not known", known);
    }

    [Fact]
    public async Task Amounts_in_different_assets_are_not_added_up()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);

        await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-3), name: "In tether", mode: TradingMode.Live);
        await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-2), name: "Also in tether", symbol: "ETHUSDT", mode: TradingMode.Live, capital: 250m);
        await application.GiveAgentAsync(
            ada.User.Id, Morning.AddDays(-1), name: "In coin", symbol: "BTCUSDC", mode: TradingMode.Live, quoteAsset: "USDC", capital: 300m);

        var ofLive = SectionOf(AgentBrowser.Read(await ada.Browser.PageAsync(Dashboard)), TradingMode.Live);

        // One total for each asset, by the name of the asset: 300 of one and 1250 of the other
        // are not 1550 of anything.
        Assert.Equal(
            [
                ("Total equity", "300 USDC"),
                ("Profit and loss today", "0 USDC"),
                ("Total equity", "1250 USDT"),
                ("Profit and loss today", "0 USDT"),
            ],
            FiguresOf(ofLive));
        Assert.Contains("Of 1 agent, with 300 USDC of allocated capital.", ofLive);
        Assert.Contains("Of 2 agents, with 1250 USDT of allocated capital.", ofLive);
        Assert.DoesNotContain("1550 USD", ofLive);
        Assert.Equal(["1000 USDT", "250 USDT", "300 USDC"], CardsOf(ofLive).Select(card => card.Equity));
    }

    [Fact]
    public async Task Nothing_of_another_user_is_on_the_dashboard()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);
        var hers = await application.GiveAgentAsync(grace.User.Id, Morning.AddDays(-3), name: "Scalp of Grace", state: AgentState.Running);
        var live = await application.GiveAgentAsync(
            grace.User.Id, Morning.AddDays(-3), name: "Live of Grace", symbol: "ETHUSDT", mode: TradingMode.Live, capital: 777.5m);

        await application.GiveOrdersAsync(grace.User.Id, hers, cycleId: null, Morning.AddHours(-2));
        await application.GiveTradeAsync(grace.User.Id, hers, Morning, closed: false);
        await application.GiveCycleAsync(grace.User.Id, live, Morning.AddHours(-1), CycleOutcome.Reduced, orders: false);
        await application.GiveNotificationAsync(grace.User.Id, hers, Morning, "Told to Grace about Demo.", mode: TradingMode.Demo);
        await application.GiveNotificationAsync(grace.User.Id, live, Morning, "Told to Grace about Live.", mode: TradingMode.Live);
        await application.GiveNotificationAsync(grace.User.Id, agentId: null, Morning, "Told to Grace about her key.", NotificationType.KeyInvalid);
        feeding.Prices.Say(Bitcoin, new WatchedPrice(100_250m, Morning));

        var before = application.RowsOf(grace.User.Id);

        // Ada has one agent of her own, and reads that alone: every part is asked for by owner.
        await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "Swing of Ada", symbol: "SOLUSDT");

        var page = AgentBrowser.Read(await ada.Browser.PageAsync(Dashboard));

        Assert.Equal([("Total equity", "1000 USDT"), ("Profit and loss today", "0 USDT")], FiguresOf(SectionOf(page, TradingMode.Demo)));
        Assert.Contains("<p>You have no agent in Live.</p>", page);
        Assert.DoesNotContain("of Grace", page);
        Assert.DoesNotContain("Told to Grace", page);
        Assert.DoesNotContain("777.5", page);
        Assert.DoesNotContain("991.8", page);
        Assert.DoesNotContain("Reduced", page);
        Assert.DoesNotContain(hers.ToString(), page);
        Assert.DoesNotContain(live.ToString(), page);
        Assert.DoesNotContain("<table", page);

        // And Grace reads her own, in each mode.
        var graces = AgentBrowser.Read(await grace.Browser.PageAsync(Dashboard));

        Assert.Equal([("Total equity", "991.8 USDT"), ("Profit and loss today", "-8.2 USDT")], FiguresOf(SectionOf(graces, TradingMode.Demo)));
        Assert.Equal([("Total equity", "777.5 USDT"), ("Profit and loss today", "0 USDT")], FiguresOf(SectionOf(graces, TradingMode.Live)));
        Assert.Contains("Told to Grace about her key.", graces);
        Assert.DoesNotContain("Swing of Ada", graces);

        // Reading the page writes nothing.
        Assert.Equal(before, application.RowsOf(grace.User.Id));
    }

    [Fact]
    public async Task An_alert_is_marked_as_read_on_the_dashboard_and_the_latest_ten_of_a_mode_are_listed()
    {
        using var feeding = new Feeding();
        var application = feeding.Application;
        var ada = await feeding.SignedInAsync(Ada);
        var grace = await feeding.SignedInAsync(Grace);
        var agent = await application.GiveAgentAsync(ada.User.Id, Morning.AddDays(-1), name: "Swing");

        // Twelve about Demo, a minute apart, one about Live, and one of another user.
        var raised = new List<Guid>();

        for (var minute = 1; minute <= 12; minute++)
        {
            raised.Add(await application.GiveNotificationAsync(
                ada.User.Id, agent, Morning.AddMinutes(-60 + minute), $"What happened in minute {minute}.", mode: TradingMode.Demo));
        }

        var ofLive = await application.GiveNotificationAsync(ada.User.Id, agentId: null, Morning, "Your Binance key for Live was refused.", NotificationType.KeyInvalid, TradingMode.Live);
        var ofGrace = await application.GiveNotificationAsync(grace.User.Id, agentId: null, Morning, "Told to Grace.", mode: TradingMode.Demo);

        var markup = await ada.Browser.PageAsync(Dashboard);
        var demo = SectionOf(AgentBrowser.Read(markup), TradingMode.Demo);

        // The latest ten of Demo, latest first, with how many there are in all.
        Assert.Contains("What the service has told you about Demo and you have not read, latest first. The latest 10 of 12.", demo);
        Assert.Equal(
            Enumerable.Range(3, 10).Reverse().Select(minute => $"What happened in minute {minute}."),
            Regex.Matches(demo, @"What happened in minute \d+\.").Select(said => said.Value));

        // Each has a form of its own, which is on the page once, and no field to type in.
        Assert.Equal(
            [$"mark-read-{ofLive}", .. Enumerable.Range(3, 10).Reverse().Select(minute => $"mark-read-{raised[minute - 1]}")],
            FormsOn(markup));
        Assert.DoesNotContain($"mark-read-{ofGrace}", markup);

        // A key of Binance for Live is an alert of Live, though it names no agent.
        Assert.Contains("Your Binance key for Live was refused.", SectionOf(AgentBrowser.Read(markup), TradingMode.Live));

        // Its user marks the newest of Demo as read: the page as it is now, by a request that
        // changes nothing, and the one before the oldest that was listed is listed in its place.
        var (address, after) = await ada.Browser.SubmitAsync(Dashboard, $"mark-read-{raised[11]}");

        Assert.Equal(Dashboard, address);

        demo = SectionOf(AgentBrowser.Read(after), TradingMode.Demo);

        Assert.Contains("The latest 10 of 11.", demo);
        Assert.DoesNotContain("What happened in minute 12.", demo);
        Assert.Contains("What happened in minute 2.", demo);
        Assert.True(await IsReadAsync(application, raised[11]));
        Assert.False(await IsReadAsync(application, raised[10]));

        // The count in the bar at the top is of all that its user has not read.
        Assert.Matches("<a href=\"/notifications\"[^>]*>Notifications \\(12\\)</a>", after);

        // The form of a notification of another user is the form of no page of Ada's, and
        // marks nothing. Neither does a form that comes without the token of the page.
        var token = Browser.AntiforgeryTokenIn(after);

        using (var notHers = await ada.Browser.SendFormAsync(Dashboard, $"mark-read-{ofGrace}", token))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notHers.StatusCode);
        }

        using (var forged = await ada.Browser.SendFormAsync(Dashboard, $"mark-read-{raised[10]}", "not the token of the page"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        }

        Assert.False(await IsReadAsync(application, ofGrace));
        Assert.False(await IsReadAsync(application, raised[10]));
    }

    [Fact]
    public async Task The_bar_at_the_top_leads_to_the_dashboard_and_a_visitor_is_sent_to_sign_in()
    {
        // The layout marks its elements for its own styles, so the link carries more than its address.
        const string link = "<a href=\"/dashboard\"[^>]*>Dashboard</a>";

        using var feeding = new Feeding();
        using var visitor = feeding.Application.CreateClient();

        // A visitor could not open it, and is not shown the way.
        Assert.DoesNotMatch(link, await visitor.PageAsync("/"));

        var (address, shown) = await visitor.OpenAsync(Dashboard);

        Assert.Equal($"{SessionAuthentication.SignInPath}?returnUrl={Uri.EscapeDataString(Dashboard)}", address);
        Assert.Contains("<h1>Sign in</h1>", shown);

        var ada = await feeding.SignedInAsync(Ada);

        Assert.Matches(link, await ada.Browser.PageAsync("/"));
        Assert.Matches(link, await ada.Browser.PageAsync("/agents"));
        Assert.Matches(link, await ada.Browser.PageAsync(Dashboard));
    }

    /// <summary>The section of a mode on the dashboard.</summary>
    private static string SectionOf(string page, TradingMode mode)
    {
        var section = Regex.Match(
            page,
            $"<section class=\"dashboard-mode\" aria-labelledby=\"dashboard-{mode.ToString().ToLowerInvariant()}-heading\">(.*?)</section>",
            RegexOptions.Singleline);

        Assert.True(section.Success, $"The dashboard has no section of {mode}:{Environment.NewLine}{page}");

        return section.Groups[1].Value;
    }

    /// <summary>The figures of a section, in the order that they are on it: the name of each, and what it says.</summary>
    private static IReadOnlyList<(string Name, string Value)> FiguresOf(string section) =>
        [
            .. Figures().Matches(section).SelectMany(figures => Named().Matches(figures.Groups[1].Value))
                .Select(named => (named.Groups[1].Value.Trim(), Said(named.Groups[2].Value))),
        ];

    /// <summary>
    /// What a figure says, as it is read: a number that counts when it changes is in an element
    /// of its own (FR-ANI-2), which says nothing by itself. MotionMarkupTests holds that element.
    /// </summary>
    private static string Said(string value) => Regex.Replace(value, "<[^>]+>", string.Empty).Trim();

    /// <summary>The cards of the agents of a section, in the order that they are on it.</summary>
    private static IReadOnlyList<Card> CardsOf(string section) =>
        [
            .. CardOfAgent().Matches(section).Select(card =>
            {
                var said = Named().Matches(card.Groups[1].Value).ToDictionary(named => named.Groups[1].Value.Trim(), named => Said(named.Groups[2].Value));

                return new Card(
                    Regex.Match(card.Groups[1].Value, "<h4><a[^>]*>(.*?)</a></h4>").Groups[1].Value,
                    Regex.Replace(Regex.Match(card.Groups[1].Value, "<p>(.*?)</p>", RegexOptions.Singleline).Groups[1].Value, "<[^>]+>", string.Empty).Trim(),
                    said["State"],
                    said["Equity"],
                    said["Today"],
                    said["Position"]);
            }),
        ];

    /// <summary>The names of the forms that a page has, in the order that they are on it.</summary>
    private static IReadOnlyList<string> FormsOn(string page) =>
        [.. Regex.Matches(page, "name=\"_handler\" value=\"(mark-[^\"]+)\"").Select(form => form.Groups[1].Value)];

    private static async Task<bool> IsReadAsync(AccountsApplication application, Guid notificationId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(Cancellation);

        return await context.Set<Notification>()
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.IsRead)
            .SingleAsync(Cancellation);
    }

    /// <summary>What the card of an agent says.</summary>
    private sealed record Card(string Name, string Trades, string State, string Equity, string Today, string Position);

    [GeneratedRegex("<dl class=\"dashboard-figures\"[^>]*>(.*?)</dl>", RegexOptions.Singleline)]
    private static partial Regex Figures();

    [GeneratedRegex(@"<dt>(.*?)</dt>\s*<dd[^>]*>(.*?)</dd>", RegexOptions.Singleline)]
    private static partial Regex Named();

    [GeneratedRegex("<li class=\"agent-card\"[^>]*>(.*?)</li>", RegexOptions.Singleline)]
    private static partial Regex CardOfAgent();
}
