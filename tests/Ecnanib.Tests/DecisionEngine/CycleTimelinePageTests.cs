using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.DecisionEngine;
using Ecnanib.RiskGuard;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.RiskGuard;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The user can view a cycle as a timeline (FR-CYC-10): each agent has a page that lists its
/// cycles, each with its outcome, and a page for each cycle that shows the summary of its data
/// package, every role output, the Risk Guard results, its orders, and its final outcome, in the
/// order they happened and in plain language (NFR-USA-2). A cycle that ends with no decision or
/// no order is shown as well. The pages take nothing. See "The timeline" in docs/team.md.
/// </summary>
public partial class CycleTimelinePageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_list_shows_each_cycle_with_its_outcome_and_leads_to_its_timeline()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing");

        // Three cycles, an hour apart: one that placed its orders, one with no decision, and
        // one that held.
        var approved = await application.GiveCycleAsync(user.Id, agent, Morning);
        var undecided = await GiveBareCycleAsync(application, user.Id, agent, Morning.AddHours(1), CycleOutcome.NoDecision);
        var held = await GiveBareCycleAsync(application, user.Id, agent, Morning.AddHours(2), CycleOutcome.Hold);

        var page = await client.PageAsync(CyclesPage(agent));
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>Cycles of the agent Swing</title>", page);
        Assert.Contains("Cycles of the agent Swing</h1>", page);

        // It says what a cycle is, and that one which decided nothing is recorded as well.
        Assert.Contains("Each time a candle of the agent's timeframe closes while the agent runs, its team runs once: a cycle.", read);
        Assert.Contains("A cycle that made no decision, or placed no order, is recorded as well.", read);
        Assert.Contains("Every cycle of the agent, latest first.", read);

        // The latest close first: each with the mode it ran in, its outcome as words, what the
        // outcome means, how long it took, and the way to its timeline.
        Assert.Equal(
            [
                new ListedCycle("17 November 2026, 11:00 UTC", "Demo", "Hold", CyclePages.Explain(CycleOutcome.Hold), "28 seconds", CyclePage(agent, held)),
                new ListedCycle(
                    "17 November 2026, 10:00 UTC", "Demo", "No decision", CyclePages.Explain(CycleOutcome.NoDecision), "28 seconds", CyclePage(agent, undecided)),
                new ListedCycle("17 November 2026, 09:00 UTC", "Demo", "Approved", CyclePages.Explain(CycleOutcome.Approved), "38 seconds", CyclePage(agent, approved)),
            ],
            ListedOn(page));
        Assert.Contains("The cycle ended with no decision and no new entry.", read);

        // An outcome is said as words, with a class beside it: it does not rely on a colour.
        Assert.Contains("<span class=\"cycle-outcome cycle-nodecision\">No decision</span>", page);

        // The page leads back to the other pages of the agent.
        Assert.Contains($"<a href=\"{AgentBrowser.AgentStatePage(agent)}\">State of the agent</a>", page);
        Assert.Contains($"<a href=\"/agents/{agent}/risk-checks\">Risk checks of the agent</a>", page);
    }

    [Fact]
    public async Task The_timeline_shows_a_cycle_from_the_close_of_its_candle_to_its_outcome()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", market: Market.Futures);
        var cycle = await application.GiveCycleAsync(user.Id, agent, Morning, CycleOutcome.Reduced);

        var page = await client.PageAsync(CyclePage(agent, cycle));
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>Cycle of the agent Swing</title>", page);
        Assert.Contains("<h1>Cycle of the agent Swing</h1>", page);

        // Which cycle it is, in which mode it ran, and how it ended, with what that means.
        Assert.Contains("The cycle for the candle that closed at 17 November 2026, 09:00 UTC, in", read);
        Assert.Contains("<strong class=\"cycle-outcome cycle-reduced\">Reduced</strong>", page);
        Assert.Contains("the Risk Manager reduced the risk of the plan", read);

        // Everything of the cycle, in the order it happened: the data package, every role
        // output, the Risk Guard result, the orders, and the final outcome.
        Assert.Equal(
            [
                "The candle closed",
                "The cycle started",
                "Data package",
                "Market Analyst",
                "Sentiment Analyst",
                "Strategist",
                "Risk Manager",
                "Risk Guard: Open a long position",
                "Entry order",
                "Stop-loss order",
                "The cycle ended: Reduced",
            ],
            Headings(read));

        // Each entry says when it happened, to the second, and how long after the close.
        Assert.Equal(
            [
                "17 November 2026, 09:00:00 UTC, as the candle closed",
                "17 November 2026, 09:00:02 UTC, 2 seconds after the candle closed",
                "17 November 2026, 09:00:03 UTC, 3 seconds after the candle closed",
                "17 November 2026, 09:00:03 UTC, 3 seconds after the candle closed",
                "17 November 2026, 09:00:03 UTC, 3 seconds after the candle closed",
                "17 November 2026, 09:00:03 UTC, 3 seconds after the candle closed",
                "17 November 2026, 09:00:03 UTC, 3 seconds after the candle closed",
                "17 November 2026, 09:00:39 UTC, 39 seconds after the candle closed",
                "17 November 2026, 09:00:39 UTC, 39 seconds after the candle closed",
                "17 November 2026, 09:00:40 UTC, 40 seconds after the candle closed",
                "17 November 2026, 09:00:40 UTC, 40 seconds after the candle closed",
            ],
            Times().Matches(read).Select(time => time.Groups[1].Value));
        Assert.Contains("The 1H candle of BTCUSDT closed.", read);

        // The summary of the data package: the agent as its prompt stated it, its limits, the
        // latest candle and the indicators of each timeframe, the open orders, and what
        // Futures adds. An indicator that had too few candles says so.
        Assert.Contains(
            "The agent traded BTCUSDT on Futures, on 1H candles. Its equity was 1000 USDT. It held no position.",
            Assert.Single(PackageAgent().Matches(read)).Groups[1].Value);
        Assert.Contains(
            "Its limits were: at most 1.5 percent of its equity at risk in a trade, a reward of at least 1.75 times the risk, a leverage of " +
            "at most 4, a liquidation buffer of at least 2.5, a daily loss limit of 5 percent, a drawdown limit of 20 percent, and an " +
            "entry at most 0.8 percent from the current price.",
            read);
        Assert.Contains("<th scope=\"col\">1H candles</th>", page);
        Assert.Contains("<th scope=\"col\">1W candles</th>", page);
        Assert.Equal(
            [
                new Measure("Closed candles in the package", ["120", "30"]),
                new Measure("Through", ["17 November 2026, 09:00 UTC", "17 November 2026, 09:00 UTC"]),
                new Measure("Open", ["60900", "58000"]),
                new Measure("High", ["61420", "61420"]),
                new Measure("Low", ["60880.25", "57500.75"]),
                new Measure("Close", ["61310", "61310"]),
                new Measure("Volume", ["1842.337", "9500.125"]),
                new Measure("Candles that the indicators were computed from", ["498", "61"]),
                new Measure("SMA20", ["61122.48", "52110.4"]),
                new Measure("SMA50", ["60310.9", "47020.15"]),
                new Measure("SMA200", ["59876.12", "not available"]),
                new Measure("EMA20", ["61190.33", "53340.92"]),
                new Measure("EMA50", ["60402.71", "48811.07"]),
                new Measure("RSI14", ["58.2", "63.41"]),
                new Measure("ATR14", ["512.75", "4120.6"]),
                new Measure("Average volume of 20 candles", ["1930.11", "8930.25"]),
            ],
            MeasuresOn(page));
        Assert.Contains("Open orders: none.", read);
        Assert.Contains(
            "Futures: a mark price of 61888.1, a funding rate of 0.01 percent, the next funding at 17 November 2026, 15:00 UTC, and an " +
            "open interest of 82450.5.",
            read);

        // Every role output: the model and its provider, how the call ended, the structured
        // result field by field with its rationale, what the call took, and the versions of
        // the templates of its prompt.
        Assert.Equal(4, Regex.Count(read, Regex.Escape($"Model: {StoredAgentRows.TeamModel}, of Claude.")));
        Assert.Equal(["Valid result", "Valid result", "Valid result", "Valid result"], CallOutcomes().Matches(page).Select(outcome => outcome.Groups[1].Value));
        Assert.Contains("The model answered, and its answer is valid: it is inside the fixed schema of the role and the allowed ranges.", read);

        var results = ResultsOn(page);

        Assert.Equal(4, results.Count);
        Assert.Equal(
            [
                new ShownField("Bias", ["bullish"]),
                new ShownField("Key levels", ["price 60000, kind support", "price 62500.5, kind resistance"]),
                new ShownField("Volatility", ["normal"]),
                new ShownField("Rationale", ["Higher lows above the 50-period average."]),
            ],
            results[0]);
        Assert.Equal(
            [
                new ShownField("Bias", ["neutral"]),
                new ShownField("Notable events", ["A spot ETF saw record inflows."]),
                new ShownField("Confidence", ["0.6"]),
                new ShownField("Live data, as the model says", ["yes"]),
                new ShownField("Rationale", ["Mixed headlines, with no clear lead."]),
            ],
            results[1]);
        Assert.Equal(
            [
                new ShownField("Action", ["open long"]),
                new ShownField("Entry type", ["market"]),
                new ShownField("Entry price", ["none"]),
                new ShownField("Stop-loss", ["59000"]),
                new ShownField("Take-profit", ["64000"]),
                new ShownField("Risk fraction", ["0.01, which is 1 percent of the agent's equity"]),
                new ShownField("Rationale", ["A pullback into support, with the trend."]),
            ],
            results[2]);
        Assert.Equal(
            [
                new ShownField("Verdict", ["approve"]),
                new ShownField("Risk fraction", ["none"]),
                new ShownField("Rationale", ["The stop is beyond the level."]),
            ],
            results[3]);

        // The mark of the sentiment report is the service's, and not the model's (FR-CYC-3):
        // the model says that it had live data, and its call ran no search.
        Assert.Contains(
            "The service marks this report \"no live data\": nothing shows that it rests on a live search, so the Strategist is told to give it no weight.",
            Assert.Single(SentimentMark().Matches(read)).Groups[1].Value);

        Assert.Equal(4, Regex.Count(read, Regex.Escape("It took 1.5 seconds. The provider counted 1200 tokens in and 150 tokens out.")));
        Assert.Equal(
            Enum.GetValues<Role>().Select(role =>
                $@"^Its prompt was composed from the instructions of the {Regex.Escape(AgentPages.Describe(role).Name)}, version \d+, " +
                @"the module of the market Futures, version \d+, the module of the timeframe 1H, version \d+, the module of the profile \w+, version \d+\.$"),
            Templates().Matches(read).Select(templates => templates.Groups[1].Value),
            (pattern, shown) => Regex.IsMatch(shown, pattern));

        // The Risk Guard result: the verdict, the size that the system computed, and every
        // rule with its outcome and why, as on the page of the risk checks.
        Assert.Equal("Accepted", Assert.Single(Verdict().Matches(page)).Groups[1].Value);
        Assert.Contains("Every rule that this plan is held to holds, so it goes on to the orders.", read);
        Assert.Contains("The system computed the size of the order, and no model chose it:", read);
        Assert.Equal(Enum.GetValues<GuardRule>().Select(RiskCheckPages.NameOf), RuleNames().Matches(page).Select(rule => AgentBrowser.Read(rule.Groups[1].Value)));

        // The orders that the cycle placed: what each does, what has become of it, and the
        // identifiers that it carries.
        Assert.Contains(
            "Buys 0.01 in BTCUSDT, at the market. It opens the position. Its position is protected by a stop-loss at 99000 and a " +
            "take-profit at 102000.",
            read);
        Assert.Contains(
            "Sells 0.01 in BTCUSDT, at the market once the price reaches 99000, and rests on the exchange until then. It closes the " +
            "position at a loss when the price reaches it.",
            read);
        Assert.Equal(["Executed", "Executed"], OrderStatuses().Matches(page).Select(status => status.Groups[1].Value));
        Assert.Contains("All of the order is executed. 0.01 of 0.01 is executed, at an average price of 100000.", read);
        Assert.Matches("It carries the identifier [0-9a-f]{32} at Binance, which knows it as the order 7001\\.", read);
        Assert.DoesNotContain("No order", Headings(read));

        // The final outcome, with what it means, and how long the cycle ran.
        Assert.Contains("with the lower risk, and with nothing else of it changed. The cycle ran for 38 seconds.", read);

        // The page leads back to the list, and to the other pages of the agent.
        Assert.Contains($"<a href=\"{CyclesPage(agent)}\">Cycles of the agent</a>", page);
        Assert.Contains($"<a href=\"{AgentBrowser.AgentStatePage(agent)}\">State of the agent</a>", page);
    }

    [Fact]
    public async Task A_cycle_that_ended_with_no_decision_or_no_order_is_shown_with_its_outcome_and_why()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        // A cycle whose Market Analyst gave no valid result: its first answer was rejected,
        // and the call that was made once more timed out. The Sentiment Analyst ran a search.
        var given = await OutputsOfAsync(application, await application.GiveCycleAsync(user.Id, agent, Morning.AddDays(-1)));
        var analyst = given.Single(output => output.Role == Role.MarketAnalyst);
        var sentiment = given.Single(output => output.Role == Role.SentimentAnalyst);
        var closedAt = Morning;
        var undecided = new DecisionCycle(
            Guid.CreateVersion7(),
            agent,
            user.Id,
            TradingMode.Demo,
            closedAt,
            closedAt.AddSeconds(1),
            closedAt.AddSeconds(70),
            CycleOutcome.NoDecision,
            CycleSummaries.Of(await AgentAsync(application, agent), closedAt, PlanAgents.Long, new OpenOrder(OpenOrderKind.StopLoss, OrderSide.Sell, 0.01m, 59500m))
                .ToJson());

        const string Odd = """{"bias":"sideways","keyLevels":[],"volatility":"normal","rationale":"<b>Range</b> & chop.","mood":"calm"}""";

        await application.CycleRecords.StoreAsync(
            undecided,
            [
                Call(analyst, undecided.Id, 1, closedAt.AddSeconds(4), RoleCallOutcome.Succeeded, RoleValidation.Rejected, "bias: NotAllowed", Odd),
                Call(sentiment, undecided.Id, 1, closedAt.AddSeconds(4), RoleCallOutcome.Succeeded, RoleValidation.Valid, null, TeamAnswers.Sentiment, liveSearches: 2),
                Call(analyst, undecided.Id, 2, closedAt.AddSeconds(8), RoleCallOutcome.TimedOut, RoleValidation.NoResult, null, null, tokens: false),
            ],
            [],
            TestContext.Current.CancellationToken);

        var page = await client.PageAsync(CyclePage(agent, undecided.Id));
        var read = AgentBrowser.Read(page);

        // It is shown like any other cycle, with its outcome: nothing of it is left out.
        Assert.Contains("<strong class=\"cycle-outcome cycle-nodecision\">No decision</strong>", page);
        Assert.Equal(
            [
                "The candle closed",
                "The cycle started",
                "Data package",
                "Market Analyst",
                "Sentiment Analyst",
                "Market Analyst, called once more",
                "No order",
                "The cycle ended: No decision",
            ],
            Headings(read));

        // The position and the order that the agent held, as its package said them.
        Assert.Contains(
            "It held a long position of 0.01, entered at 60000, with a stop-loss at 59500 and a take-profit at 64000.", read);
        Assert.Contains("Stop-loss: sell 0.01 at 59500", read);

        // Why each call gave no valid result, in plain language. A rejected answer is shown
        // as the model wrote it, also a field that no schema defines.
        Assert.Equal(["Rejected result", "Valid result", "Timed out"], CallOutcomes().Matches(page).Select(outcome => outcome.Groups[1].Value));
        Assert.Contains(
            "The model answered, and its answer was rejected. Its field bias has a value that is none of those that the schema lists " +
            "for it. A rejected answer is rejected whole: nothing of it is used.",
            read);
        Assert.Contains("The provider did not answer within its time limit.", read);
        Assert.Contains("It took 1.5 seconds. The provider counted no tokens.", read);

        var results = ResultsOn(page);

        Assert.Equal(2, results.Count);
        Assert.Equal(
            [
                new ShownField("Bias", ["sideways"]),
                new ShownField("Key levels", ["none"]),
                new ShownField("Volatility", ["normal"]),
                new ShownField("Rationale", ["<b>Range</b> & chop."]),
                new ShownField("mood", ["calm"]),
            ],
            results[0]);

        // What a model wrote is text on the page, and never markup of it.
        Assert.Contains("&lt;b&gt;Range&lt;/b&gt; &amp; chop.", page);
        Assert.DoesNotContain("<b>Range</b>", page);

        // A report whose call ran a search is marked as resting on live data.
        Assert.Contains("The service marks this report \"live data\": its model ran a live search for it.", read);
        Assert.Contains("It ran 2 live searches.", read);

        // No plan went on, so there is no check and no order, and the page says so.
        Assert.Empty(Verdict().Matches(page));
        Assert.Equal("The cycle placed no order: nothing of a plan went on to the Risk Guard.", Assert.Single(NoOrder().Matches(read)).Groups[1].Value);
        Assert.Contains("A role gave no valid result, also when it was called once more.", read);
        Assert.Contains("The cycle ran for 69 seconds.", read);

        // A cycle whose plan the Risk Guard accepted, and that placed no entry, says why.
        var skipped = await application.GiveCycleAsync(
            user.Id, agent, Morning.AddHours(1), CycleOutcome.Approved, orders: false, noEntry: NoEntryReason.QuoteBalance);
        var noEntry = AgentBrowser.Read(await client.PageAsync(CyclePage(agent, skipped)));

        Assert.Equal("No order", Headings(noEntry)[^2]);
        Assert.Equal(
            "The Risk Guard accepted the plan, and no entry was placed. Your Binance account held less of the asset that the symbol is " +
            "priced in than the entry needed.",
            Assert.Single(NoOrder().Matches(noEntry)).Groups[1].Value);

        // A cycle that ended before its package was built says that it kept none, and why.
        var halted = await GiveBareCycleAsync(application, user.Id, agent, Morning.AddHours(2), CycleOutcome.Halted);
        var stopped = AgentBrowser.Read(await client.PageAsync(CyclePage(agent, halted)));

        Assert.Equal(
            ["The candle closed", "The cycle started", "Data package", "No order", "The cycle ended: Halted"],
            Headings(stopped));
        Assert.Contains("No data package was built: a loss limit halted the agent before its team was called.", stopped);
        Assert.Contains("The candle that the cycle is for closed.", stopped);

        // An entry that stands for something that did not happen says no time of its own.
        Assert.Equal(3, Times().Matches(stopped).Count);
    }

    [Fact]
    public void Every_outcome_of_a_cycle_a_call_and_an_order_is_explained_in_plain_language()
    {
        // How a cycle ended: a word or two of its own, and a sentence.
        var outcomes = Enum.GetValues<CycleOutcome>();

        Assert.Equal(outcomes.Length, outcomes.Select(CyclePages.Describe).Distinct().Count());
        Assert.Equal(outcomes.Length, outcomes.Select(CyclePages.Explain).Distinct().Count());
        Assert.All(outcomes, outcome =>
        {
            Sentences(CyclePages.Explain(outcome));
            Sentences(CyclePages.NoPackage(outcome));
            Assert.DoesNotContain(outcome.ToString(), CyclePages.Explain(outcome), StringComparison.Ordinal);
        });

        // Why no entry was placed.
        var reasons = Enum.GetValues<NoEntryReason>();

        Assert.Equal(reasons.Length, reasons.Select(CyclePages.Explain).Distinct().Count());
        Assert.All(reasons, reason =>
        {
            Sentences(CyclePages.Explain(reason));
            Assert.DoesNotContain(reason.ToString(), CyclePages.Explain(reason), StringComparison.Ordinal);
        });

        // Why an answer was rejected, for a field and for the answer as a whole.
        var rules = Enum.GetValues<RejectionRule>();

        Assert.All(rules, rule =>
        {
            foreach (var field in new[] { "keyLevels[1].price", RoleRejection.Answer })
            {
                var says = CyclePages.Rejected(new RoleRejection(rule, field).ToString());

                Sentences(says);
                Assert.DoesNotContain(rule.ToString(), says, StringComparison.Ordinal);
            }
        });
        Assert.Equal(rules.Length, rules.Select(rule => CyclePages.Rejected($"riskFraction: {rule}")).Distinct().Count());
        Sentences(CyclePages.Rejected(null));
        Sentences(CyclePages.Rejected("something that nothing of the application wrote"));

        // How a call ended, whatever was made of its answer.
        foreach (var outcome in Enum.GetValues<RoleCallOutcome>())
        {
            foreach (var validation in Enum.GetValues<RoleValidation>())
            {
                var call = new RoleCallView(
                    Role.Strategist, 1, "Claude", "claude-test-model", null, [], Morning, 1500, null, null, null, outcome, validation, "action: NotOnMarket", null);

                Sentences(CyclePages.Says(call));
                Sentences(CyclePages.Usage(call));
                Assert.NotEmpty(CyclePages.Describe(outcome, validation));
                Assert.DoesNotContain(outcome.ToString(), CyclePages.Says(call), StringComparison.Ordinal);
            }
        }

        Assert.Equal(
            Enum.GetValues<RoleCallOutcome>().Length,
            Enum.GetValues<RoleCallOutcome>().Select(outcome => CyclePages.Describe(outcome, RoleValidation.NoResult)).Distinct().Count());

        // What an order does, and what has become of it, for every kind, purpose, and status.
        foreach (var purpose in Enum.GetValues<OrderPurpose>())
        {
            foreach (var type in Enum.GetValues<OrderType>())
            {
                foreach (var status in Enum.GetValues<OrderStatus>())
                {
                    foreach (var price in new decimal?[] { 59000m, null })
                    {
                        var order = new CycleOrderView(
                            purpose, type, OrderSide.Sell, "BTCUSDT", price, price, null, 0.01m, status, 0.004m, price, "0a1b", null, Morning, Morning);

                        Sentences(CyclePages.Says(order));
                        Sentences(CyclePages.Became(order));
                        Assert.DoesNotContain(status.ToString() + " ", CyclePages.Became(order) + " ", StringComparison.Ordinal);
                    }
                }
            }
        }

        Assert.Equal(
            Enum.GetValues<OrderStatus>().Length,
            Enum.GetValues<OrderStatus>().Select(CyclePages.Describe).Distinct().Count());
        Assert.Equal(
            Enum.GetValues<OrderPurpose>().Length,
            Enum.GetValues<OrderPurpose>().Select(CyclePages.Describe).Distinct().Count());

        // The versions of the templates, for every kind.
        Assert.All(
            Enum.GetValues<PromptTemplateKind>(),
            kind => Assert.EndsWith(", version 3", CyclePages.Says(new TemplateVersion(kind, "Strategist", 3)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cycle_says_the_mode_that_the_agent_was_in_when_it_ran()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // An agent that is in Demo now, and was in Live for one of its cycles.
        var agent = await application.GiveAgentAsync(user.Id, Morning);
        var inLive = await GiveBareCycleAsync(application, user.Id, agent, Morning, CycleOutcome.Hold, TradingMode.Live);
        var inDemo = await GiveBareCycleAsync(application, user.Id, agent, Morning.AddHours(1), CycleOutcome.Hold);

        // The badge of the agent under the heading, and the mode of each cycle in its row:
        // what an agent did in Live is never read as Demo (FR-BIN-11).
        var list = await client.PageAsync(CyclesPage(agent));

        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", list);
        Assert.Equal(["Demo", "Live"], ListedOn(list).Select(cycle => cycle.Mode));

        // And on the timeline of each.
        var live = await client.PageAsync(CyclePage(agent, inLive));
        var demo = await client.PageAsync(CyclePage(agent, inDemo));

        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", live);
        Assert.Contains($"09:00 UTC, in {AgentBrowser.Badge(TradingMode.Live)}. Outcome:", live);
        Assert.Contains($"10:00 UTC, in {AgentBrowser.Badge(TradingMode.Demo)}. Outcome:", demo);
        Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Live), demo);
    }

    [Fact]
    public async Task The_page_of_an_agent_with_no_cycle_says_so_and_a_page_of_many_lists_the_latest()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        var none = await client.PageAsync(CyclesPage(agent));

        Assert.Contains("No cycle of this agent has been recorded yet.", AgentBrowser.Read(none));
        Assert.Empty(ListedOn(none));
        Assert.DoesNotContain("<table>", none);

        // 52 cycles, an hour apart.
        for (var hour = 0; hour < 52; hour++)
        {
            await GiveBareCycleAsync(application, user.Id, agent, Morning.AddHours(hour), CycleOutcome.Hold);
        }

        var many = await client.PageAsync(CyclesPage(agent));

        // The latest 50, from the latest close down.
        Assert.Contains("The latest 50 of 52 cycles, latest first.", AgentBrowser.Read(many));
        Assert.Equal(50, ListedOn(many).Count);
        Assert.Equal("19 November 2026, 12:00 UTC", ListedOn(many)[0].ClosedAt);
        Assert.DoesNotContain("No cycle of this agent has been recorded yet.", AgentBrowser.Read(many));
    }

    [Fact]
    public async Task The_pages_of_an_agent_lead_to_its_cycles_and_a_check_to_the_timeline_of_its_cycle()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);
        var cycle = await application.GiveCycleAsync(user.Id, agent, Morning);

        Assert.Contains(
            $"<a href=\"{CyclesPage(agent)}\">Cycles of the agent</a>",
            await client.PageAsync(AgentBrowser.AgentStatePage(agent)));

        // The page of the risk checks shows a check, and leads to the cycle that it was made in.
        var checks = await client.PageAsync($"/agents/{agent}/risk-checks");

        Assert.Contains($"<a href=\"{CyclePage(agent, cycle)}\">Timeline of the cycle of this check</a>", checks);
        Assert.Contains($"<a href=\"{CyclesPage(agent)}\">Cycles of the agent</a>", checks);
    }

    [Fact]
    public async Task The_cycles_of_an_agent_of_another_user_are_not_found()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var grace = await application.RegisterVerifiedAsync(Grace);
        var gracesAgent = await application.GiveAgentAsync(grace.Id, Morning, name: "Grace's swing");
        var gracesCycle = await application.GiveCycleAsync(grace.Id, gracesAgent, Morning);

        var ada = await application.RegisterVerifiedAsync(Ada);
        var adasAgent = await application.GiveAgentAsync(ada.Id, Morning, name: "Adas");
        var adasOther = await application.GiveAgentAsync(ada.Id, Morning, name: "Other", symbol: "SOLUSDT");
        var ofOther = await application.GiveCycleAsync(ada.Id, adasOther, Morning);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // As for an agent and a cycle that do not exist: nothing tells her that the id is
        // someone's. A cycle of Grace is not found under an agent of Ada either, and neither
        // is a cycle of another agent of her own.
        foreach (var path in new[]
        {
            CyclesPage(gracesAgent),
            CyclePage(gracesAgent, gracesCycle),
            CyclePage(adasAgent, gracesCycle),
            CyclePage(adasAgent, ofOther),
            CyclePage(adasAgent, Guid.CreateVersion7()),
        })
        {
            using var answer = await client.GetAsync(path, TestContext.Current.CancellationToken);
            var shown = AgentBrowser.Read(await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
            Assert.DoesNotContain("Grace", shown);
            Assert.DoesNotContain("Data package", shown);
            Assert.DoesNotContain("Rationale", shown);
        }

        // Her own cycle is found under its own agent, and Grace reads hers.
        Assert.Contains("Data package", await client.PageAsync(CyclePage(adasOther, ofOther)));

        using var gracesBrowser = application.CreateClient();
        await gracesBrowser.SignInAsync(Grace);

        Assert.Single(ListedOn(await gracesBrowser.PageAsync(CyclesPage(gracesAgent))));
        Assert.Contains("Data package", await gracesBrowser.PageAsync(CyclePage(gracesAgent, gracesCycle)));
    }

    [Fact]
    public async Task The_pages_offer_no_way_to_change_a_cycle_and_take_none()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);
        var cycle = await application.GiveCycleAsync(user.Id, agent, Morning);
        var before = application.RowsOf(user.Id);

        foreach (var path in new[] { CyclesPage(agent), CyclePage(agent, cycle) })
        {
            var page = await client.PageAsync(path);

            // The one form of a page is the button of the bar at the top, which signs out. A
            // user reads the record of a cycle, and changes none.
            var form = Assert.Single(Form().Matches(page));

            Assert.Contains("action=\"/sign-out\"", form.Value);
            Assert.DoesNotContain("<textarea", page);
            Assert.DoesNotContain("<select", page);
            Assert.DoesNotContain("contenteditable", page);
            Assert.All(Input().Matches(page), input => Assert.Contains("type=\"hidden\"", input.Value));

            // A form that is posted at it all the same is the form of no page.
            var token = Browser.AntiforgeryTokenIn(page);

            foreach (var name in new[] { "delete-cycle", "accept-plan", "place-order" })
            {
                using var posted = await client.SendFormAsync(path, name, token, ("Outcome", "Approved"), ("Verdict", "Accepted"));

                Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
            }
        }

        Assert.Equal(before, application.RowsOf(user.Id));
    }

    /// <summary>The page that lists the cycles of an agent.</summary>
    private static string CyclesPage(Guid agentId) => $"/agents/{agentId}/cycles";

    /// <summary>The page of the timeline of one cycle of an agent.</summary>
    private static string CyclePage(Guid agentId, Guid cycleId) => $"/agents/{agentId}/cycles/{cycleId}";

    /// <summary>A sentence, or several, in words: it ends as one, and has no gap where a number or a name would stand.</summary>
    private static void Sentences(string says)
    {
        Assert.EndsWith(".", says, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", says, StringComparison.Ordinal);
        Assert.DoesNotMatch(@" (at|is|of|in|the|a) [.,]", says);
    }

    /// <summary>Gives an agent a cycle that called no role and checked no plan: the row of the cycle alone, with no summary.</summary>
    private static async Task<Guid> GiveBareCycleAsync(
        AccountsApplication application,
        Guid userId,
        Guid agentId,
        DateTimeOffset closedAt,
        CycleOutcome outcome,
        TradingMode mode = TradingMode.Demo)
    {
        var cycle = new DecisionCycle(Guid.CreateVersion7(), agentId, userId, mode, closedAt, closedAt.AddSeconds(2), closedAt.AddSeconds(30), outcome);

        await application.CycleRecords.StoreAsync(cycle, [], [], TestContext.Current.CancellationToken);

        return cycle.Id;
    }

    /// <summary>The calls of a cycle, as they are stored.</summary>
    private static async Task<IReadOnlyList<RoleOutput>> OutputsOfAsync(AccountsApplication application, Guid cycleId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<RoleOutput>().AsNoTracking().Where(output => output.CycleId == cycleId).ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>An agent, as it is stored.</summary>
    private static async Task<Ecnanib.Agents.Agent> AgentAsync(AccountsApplication application, Guid agentId)
    {
        await using var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<Ecnanib.Agents.Agent>().AsNoTracking().SingleAsync(agent => agent.Id == agentId, TestContext.Current.CancellationToken);
    }

    /// <summary>A call of the role of another call, with its model and its templates, in a cycle that the test names, which ended as the test says.</summary>
    private static RoleOutput Call(
        RoleOutput like,
        Guid cycleId,
        int attempt,
        DateTimeOffset calledAt,
        RoleCallOutcome outcome,
        RoleValidation validation,
        string? rejection,
        string? result,
        int? liveSearches = null,
        bool tokens = true) =>
        new(
            Guid.CreateVersion7(),
            cycleId,
            like.UserId,
            like.Role,
            attempt,
            like.ModelId,
            like.RoleTemplateId,
            like.MarketTemplateId,
            like.TimeframeTemplateId,
            like.ProfileTemplateId,
            calledAt,
            like.LatencyMs,
            tokens ? like.InputTokens : null,
            tokens ? like.OutputTokens : null,
            liveSearches,
            outcome,
            validation,
            rejection,
            result);

    /// <summary>The headings of the entries of a timeline, in the order of the page.</summary>
    private static IReadOnlyList<string> Headings(string read) => [.. Heading().Matches(read).Select(heading => heading.Groups[1].Value)];

    /// <summary>The cycles that a list shows, in the order of the page.</summary>
    private static IReadOnlyList<ListedCycle> ListedOn(string page) =>
    [
        .. Listed().Matches(page).Select(row => new ListedCycle(
            AgentBrowser.Read(row.Groups[1].Value),
            row.Groups[2].Value,
            AgentBrowser.Read(row.Groups[3].Value),
            AgentBrowser.Read(row.Groups[4].Value),
            AgentBrowser.Read(row.Groups[5].Value),
            row.Groups[6].Value,
            row.Groups["kind"].Value)),
    ];

    /// <summary>The rows of the table of a data package, each with its value for every series.</summary>
    private static IReadOnlyList<Measure> MeasuresOn(string page)
    {
        var table = PackageTable().Match(page);

        Assert.True(table.Success, "The page has no table of a data package.");

        return
        [
            .. MeasureRow().Matches(table.Value).Select(row => new Measure(
                AgentBrowser.Read(row.Groups[1].Value),
                [.. Cell().Matches(row.Groups[2].Value).Select(cell => AgentBrowser.Read(cell.Groups[1].Value))])),
        ];
    }

    /// <summary>The structured results that a timeline shows, in the order of the page, each field by field.</summary>
    private static IReadOnlyList<IReadOnlyList<ShownField>> ResultsOn(string page) =>
    [
        .. Result().Matches(page).Select(result => (IReadOnlyList<ShownField>)
        [
            .. Field().Matches(result.Groups[1].Value).Select(field => new ShownField(
                AgentBrowser.Read(field.Groups[1].Value),
                [.. Value().Matches(field.Groups[2].Value).Select(value => AgentBrowser.Read(value.Groups[1].Value))])),
        ]),
    ];

    /// <summary>A cycle as a list shows it.</summary>
    /// <remarks>
    /// A cycle of these tests was started by the close of a candle, and is listed as scheduled:
    /// a test cycle, which its user runs, is in <see cref="TestCyclePageTests"/> (FR-CYC-12).
    /// </remarks>
    private sealed record ListedCycle(
        string ClosedAt, string Mode, string Outcome, string Means, string Took, string Timeline, string Kind = "Scheduled");

    /// <summary>A row of the table of a data package: what it measures, and its value for each series.</summary>
    private sealed record Measure(string Name, IReadOnlyList<string> Values)
    {
        public bool Equals(Measure? other) => other is not null && Name == other.Name && Values.SequenceEqual(other.Values);

        public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>A field of a structured result as a timeline shows it: its name, and its values.</summary>
    private sealed record ShownField(string Name, IReadOnlyList<string> Values)
    {
        public bool Equals(ShownField? other) => other is not null && Name == other.Name && Values.SequenceEqual(other.Values);

        public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<h2[^>]*>([^<]*)</h2>")]
    private static partial Regex Heading();

    [GeneratedRegex(@"<p class=""timeline-time"">([^<]*)</p>")]
    private static partial Regex Times();

    [GeneratedRegex(@"<p class=""package-agent"">([^<]*)</p>")]
    private static partial Regex PackageAgent();

    [GeneratedRegex(@"<p class=""sentiment-mark"">([^<]*)</p>")]
    private static partial Regex SentimentMark();

    [GeneratedRegex(@"<p class=""call-templates"">([^<]*)</p>")]
    private static partial Regex Templates();

    [GeneratedRegex(@"<p class=""no-order"">([^<]*)</p>")]
    private static partial Regex NoOrder();

    [GeneratedRegex(@"<strong class=""call-outcome"">([^<]*)</strong>")]
    private static partial Regex CallOutcomes();

    [GeneratedRegex(@"<strong class=""order-status"">([^<]*)</strong>")]
    private static partial Regex OrderStatuses();

    [GeneratedRegex(@"<strong class=""guard-verdict"">([^<]*)</strong>")]
    private static partial Regex Verdict();

    [GeneratedRegex(@"<tr>\s*<th scope=""row"">([^<]*)</th>\s*<td><span class=""rule-outcome")]
    private static partial Regex RuleNames();

    [GeneratedRegex(
        @"<tr>\s*<th scope=""row"">([^<]*)</th>\s*<td><span class=""cycle-kind cycle-kind-[a-z]+"">(?<kind>[^<]*)</span></td>\s*" +
        @"<td><span class=""mode-badge mode-[a-z]+"">(Demo|Live)</span></td>\s*" +
        @"<td><span class=""cycle-outcome cycle-[a-z]+"">([^<]*)</span></td>\s*<td>([^<]*)</td>\s*<td>([^<]*)</td>\s*" +
        @"<td><a href=""([^""]*)"">Timeline</a></td>\s*</tr>")]
    private static partial Regex Listed();

    [GeneratedRegex(@"<div class=""agents package-series"".*?</table>", RegexOptions.Singleline)]
    private static partial Regex PackageTable();

    [GeneratedRegex(@"<tr>\s*<th scope=""row"">([^<]*)</th>((?:\s*<td>[^<]*</td>)+)\s*</tr>")]
    private static partial Regex MeasureRow();

    [GeneratedRegex(@"<td>([^<]*)</td>")]
    private static partial Regex Cell();

    [GeneratedRegex(@"<dl class=""role-result"">(.*?)</dl>", RegexOptions.Singleline)]
    private static partial Regex Result();

    [GeneratedRegex(@"<dt>([^<]*)</dt>((?:\s*<dd>[^<]*</dd>)+)")]
    private static partial Regex Field();

    [GeneratedRegex(@"<dd>([^<]*)</dd>")]
    private static partial Regex Value();

    [GeneratedRegex("<form[^>]*>")]
    private static partial Regex Form();

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex Input();
}
