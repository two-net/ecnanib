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

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Each Risk Guard check stores the outcome of every rule, so a user can see exactly why a plan
/// was accepted or rejected (FR-RSK-9): each agent has a page that shows its checks, each with
/// its verdict and with every rule, its outcome, and why, in plain language (NFR-USA-2). The
/// page takes nothing. See "The page" in docs/risk-guard.md.
/// </summary>
public partial class RiskCheckPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A plan to open long on Futures whose reward is 1.4 times its risk, and whose order has no size.</summary>
    private static readonly GuardCase Rejected = GuardCases.OpenLongOnFutures with { TakeProfit = 61400m, Size = null };

    [Fact]
    public async Task The_page_shows_exactly_why_a_plan_was_accepted_or_rejected()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", market: Market.Futures);

        // A plan to open long, which the Risk Guard rejected, and an hour later a plan to close,
        // which it accepted.
        await GiveCheckAsync(application, user.Id, agent, Morning, Rejected, PlanAction.OpenLong);
        await GiveCheckAsync(application, user.Id, agent, Morning.AddHours(1), GuardCases.Close, PlanAction.Close);

        var page = await client.PageAsync(RiskChecksPage(agent));
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>Risk checks of the agent Swing</title>", page);
        Assert.Contains("<h1>Risk checks of the agent Swing</h1>", page);

        // It says what the Risk Guard is: fixed rules, no AI, and nothing that switches them
        // off, and why a plan that reduces risk is held to few of them.
        Assert.Contains("is checked against fixed rules: the Risk Guard. The rules are no AI.", read);
        Assert.Contains("nothing switches them off: no answer of a model, and no setting", read);
        Assert.Contains("so that risk can always be reduced", read);
        Assert.Contains("Every check of the agent's plans, latest first.", read);

        // The latest check first: each under the close of its candle and what its plan does.
        Assert.Equal(
            ["17 November 2026, 10:00 UTC: Close the position", "17 November 2026, 09:00 UTC: Open a long position"],
            CheckHeading().Matches(read).Select(heading => heading.Groups[1].Value));

        // Each with its verdict as a word, and the mode that the agent was in.
        Assert.Equal(["Accepted", "Rejected"], Verdict().Matches(page).Select(verdict => verdict.Groups[1].Value));
        Assert.Equal(2, Regex.Count(page, Regex.Escape($", in {AgentBrowser.Badge(TradingMode.Demo)}, for the")));
        Assert.Contains("Every rule that this plan is held to holds, so it goes on to the orders.", read);
        Assert.Contains("The plan does not go on: a rule does not hold, or is not known to hold. No position is opened.", read);

        // Every rule of each check, in the order of the rules, with its outcome as words.
        var checks = RulesOn(page);

        Assert.Equal(2, checks.Count);
        Assert.All(checks, rules => Assert.Equal(
            Enum.GetValues<GuardRule>().Select(RiskCheckPages.NameOf), rules.Select(rule => rule.Name)));

        // Why the plan to open was rejected: the ratio does not hold, with the numbers, and
        // what needs the size of the order is not known.
        var rejected = checks[1];

        Assert.Equal(
            [
                new ShownRule("Stop-loss on the correct side", "Holds", "The stop-loss at 59000 is on the losing side of the entry at 60000."),
                new ShownRule(
                    "Reward-to-risk ratio",
                    "Does not hold",
                    "The take-profit is only 1.4 times as far from the entry as the stop-loss. The agent asks for at least 1.5."),
                new ShownRule("Distance of the entry", "Holds", "The entry is 0 percent from the current price. The agent allows up to 1 percent."),
                new ShownRule("Price step", "Holds", "Every price of the plan is a multiple of the price step of the symbol, 0.1."),
                new ShownRule("Quantity step", "Not known", "The quantity of the order is not known."),
                new ShownRule("Least value of an order", "Not known", "The value of the entry is not known: its quantity or its price is not known."),
                new ShownRule("Open position", "Holds", "The agent holds no position, so it can open one."),
                new ShownRule("Protection only tightens", "Not asked", "Asked only of a plan that adjusts the protection."),
                new ShownRule(
                    "Daily loss limit", "Holds", "The agent has lost 0 percent of its equity in the UTC day. Its daily loss limit is 6 percent."),
                new ShownRule("Drawdown limit", "Holds", "The equity of the agent is 0 percent below its peak. Its drawdown limit is 25 percent."),
                new ShownRule(
                    "Stale limit",
                    "Holds",
                    "The plan was checked 30 seconds after the candle closed. The stale limit of the agent's timeframe is 120 seconds."),
                new ShownRule("Mode", "Holds", "You have a valid Binance key for the environment that the agent trades in."),
                new ShownRule("Market", "Holds", "Your Binance key may trade on the market of the agent."),
                new ShownRule("Symbol", "Holds", "The market has the symbol of the agent, and the exchange takes orders in it."),
                new ShownRule("Global halt", "Holds", "No global halt is on."),
                new ShownRule("Liquidation buffer", "Not known", "The leverage of the order is not known, or its entry is not."),
            ],
            rejected);

        // Why the plan to close was accepted: the few rules that it is held to hold, and no
        // other rule is asked of it.
        var accepted = checks[0];
        const string NotAsked = "Not asked of a plan that only closes the position or adjusts its protection, so that risk can always be reduced.";

        Assert.Equal(
            [
                new ShownRule("Price step", "Holds", "The plan names no price that the exchange holds to a step."),
                new ShownRule("Quantity step", "Holds", "The quantity is a multiple of the quantity step of the symbol, 0.001."),
                new ShownRule("Open position", "Holds", "The agent holds the position that the plan acts on."),
            ],
            accepted.Where(rule => rule.Outcome == "Holds"));
        Assert.Equal(13, accepted.Count(rule => rule.Outcome == "Not asked"));
        Assert.Equal(12, accepted.Count(rule => rule.Why == NotAsked));

        // An outcome is said as words, with a class beside it: it does not rely on a colour.
        Assert.Contains("<span class=\"rule-outcome rule-failed\">Does not hold</span>", page);
        Assert.Contains("<span class=\"rule-outcome rule-unknown\">Not known</span>", page);

        // The page leads back to the other pages of the agent.
        Assert.Contains($"<a href=\"{AgentBrowser.AgentStatePage(agent)}\">State of the agent</a>", page);
        Assert.Contains($"<a href=\"{AgentBrowser.AgentPage(agent)}\">Settings of the agent</a>", page);
    }

    [Fact]
    public async Task Of_a_reverse_that_was_rejected_the_page_shows_both_checks_and_says_that_the_close_goes_on()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, market: Market.Futures);

        var cycle = Cycle(agent, user.Id, Morning);

        // A global halt is on: what the reverse would open is rejected, and its close goes on.
        await application.CycleRecords.StoreAsync(
            cycle,
            [],
            [
                GuardedCycles.Check(cycle.Id, user.Id, Morning.AddSeconds(30), GuardCases.Reverse with { GlobalHalt = true }, PlanAction.Reverse),
                GuardedCycles.Check(cycle.Id, user.Id, Morning.AddSeconds(30), GuardCases.Close, PlanAction.Close, sequence: 2),
            ],
            TestContext.Current.CancellationToken);

        var page = await client.PageAsync(RiskChecksPage(agent));
        var read = AgentBrowser.Read(page);

        // The reverse first, and then its close, under the same close of a candle.
        Assert.Equal(
            ["17 November 2026, 09:00 UTC: Reverse the position", "17 November 2026, 09:00 UTC: Close the position"],
            CheckHeading().Matches(read).Select(heading => heading.Groups[1].Value));
        Assert.Equal(["Rejected", "Accepted"], Verdict().Matches(page).Select(verdict => verdict.Groups[1].Value));

        Assert.Contains("What it would open is rejected, and its close is checked by itself in the next check.", read);
        Assert.Contains(
            "This is the close of a plan to reverse, of which what it would open was rejected. Closing reduces risk, so the close goes on by itself.",
            read);

        // The rejection is explained in plain language where it appears.
        Assert.Contains(
            new ShownRule(
                "Global halt",
                "Does not hold",
                "A global halt is on. While it is, no agent opens a new position. Open positions are still managed and protected."),
            RulesOn(page)[0]);
    }

    [Fact]
    public void Every_rule_and_every_outcome_is_explained_in_plain_language()
    {
        // Whatever a check stored for a rule, the page has words for it: with both numbers,
        // with one of them, and with none, for a plan of every kind.
        var rules = Enum.GetValues<GuardRule>();
        (decimal? Measured, decimal? Threshold)[] numbers = [(1.25m, 2.5m), (1.25m, null), (null, 2.5m), (null, null)];

        Assert.Equal(rules.Length, rules.Select(RiskCheckPages.NameOf).Distinct().Count());

        foreach (var rule in rules)
        {
            foreach (var outcome in Enum.GetValues<RuleOutcome>())
            {
                foreach (var (measured, threshold) in numbers)
                {
                    foreach (var action in Enum.GetValues<PlanAction>())
                    {
                        var says = RiskCheckPages.Says(new RuleResult(rule, outcome, measured, threshold), action);

                        // A sentence, in words: no name of the code, and no number that is not there.
                        Assert.EndsWith(".", says, StringComparison.Ordinal);
                        Assert.DoesNotContain(rule.ToString(), says, StringComparison.Ordinal);
                        Assert.DoesNotContain("  ", says, StringComparison.Ordinal);
                        Assert.DoesNotMatch(@" (at|is|of|only|least|limit of|step of the symbol,|worth|price|quantity) [.,]", says);
                    }
                }
            }

            // The name of a rule is words as well.
            Assert.DoesNotContain(rule.ToString(), RiskCheckPages.NameOf(rule), StringComparison.Ordinal);
        }

        // Each outcome and each verdict has a word of its own, and each action a description.
        Assert.Equal(["Holds", "Does not hold", "Not known", "Not asked"], Enum.GetValues<RuleOutcome>().Select(RiskCheckPages.Describe));
        Assert.Equal(["Accepted", "Rejected"], Enum.GetValues<GuardVerdict>().Select(RiskCheckPages.Describe));
        Assert.Equal(
            Enum.GetValues<PlanAction>().Length,
            Enum.GetValues<PlanAction>().Select(RiskCheckPages.Describe).Distinct().Count());
    }

    [Fact]
    public async Task The_page_of_an_agent_with_no_check_says_so_and_a_page_of_many_shows_the_latest()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        var none = AgentBrowser.Read(await client.PageAsync(RiskChecksPage(agent)));

        Assert.Contains("No plan of this agent has been checked yet.", none);
        Assert.Empty(CheckHeading().Matches(none));

        // 23 checks, an hour apart.
        for (var hour = 0; hour < 23; hour++)
        {
            await GiveCheckAsync(application, user.Id, agent, Morning.AddHours(hour), GuardCases.OpenLong, PlanAction.OpenLong);
        }

        var many = AgentBrowser.Read(await client.PageAsync(RiskChecksPage(agent)));

        // The latest 20, from the latest close down.
        Assert.Contains("The latest 20 of 23 checks, latest first.", many);
        Assert.Equal(20, CheckHeading().Matches(many).Count);
        Assert.StartsWith("18 November 2026, 07:00 UTC", CheckHeading().Matches(many)[0].Groups[1].Value, StringComparison.Ordinal);
        Assert.DoesNotContain("No plan of this agent has been checked yet.", many);
    }

    [Fact]
    public async Task A_check_says_the_mode_that_the_agent_was_in_when_it_was_made()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // An agent that is in Demo now, and was in Live for one of its cycles.
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        await GiveCheckAsync(application, user.Id, agent, Morning, GuardCases.OpenLong, PlanAction.OpenLong, TradingMode.Live);
        await GiveCheckAsync(application, user.Id, agent, Morning.AddHours(1), GuardCases.OpenLong, PlanAction.OpenLong);

        var page = await client.PageAsync(RiskChecksPage(agent));

        // The badge of the agent under the heading, and the mode of each check beside its
        // verdict: what an agent did in Live is never read as Demo (FR-BIN-11).
        Assert.Matches($@"</h1>\s*<p>Mode: {Regex.Escape(AgentBrowser.Badge(TradingMode.Demo))} ", page);
        Assert.Equal(
            [$", in {AgentBrowser.Badge(TradingMode.Demo)}, for the", $", in {AgentBrowser.Badge(TradingMode.Live)}, for the"],
            CheckMode().Matches(page).Select(mode => mode.Value));
    }

    [Fact]
    public async Task The_pages_of_an_agent_lead_to_its_risk_checks()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        Assert.Contains(
            $"<a href=\"{RiskChecksPage(agent)}\">Risk checks of the agent</a>",
            await client.PageAsync(AgentBrowser.AgentStatePage(agent)));
    }

    [Fact]
    public async Task The_risk_checks_of_an_agent_of_another_user_are_not_found()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        var grace = await application.RegisterVerifiedAsync(Grace);
        var gracesAgent = await application.GiveAgentAsync(grace.Id, Morning, name: "Grace's swing");

        await GiveCheckAsync(application, grace.Id, gracesAgent, Morning, Rejected, PlanAction.OpenLong);

        await application.RegisterVerifiedAsync(Ada);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // As for an agent that does not exist: nothing tells her that the id is someone's.
        using var answer = await client.GetAsync(RiskChecksPage(gracesAgent), TestContext.Current.CancellationToken);
        var shown = await answer.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.DoesNotContain("Grace", AgentBrowser.Read(shown));
        Assert.Empty(RulesOn(shown));

        // Grace reads them.
        using var gracesBrowser = application.CreateClient();
        await gracesBrowser.SignInAsync(Grace);

        Assert.Single(RulesOn(await gracesBrowser.PageAsync(RiskChecksPage(gracesAgent))));
    }

    [Fact]
    public async Task The_page_offers_no_way_to_change_a_check_or_a_rule_and_takes_none()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning);

        await GiveCheckAsync(application, user.Id, agent, Morning, Rejected, PlanAction.OpenLong);

        var path = RiskChecksPage(agent);
        var page = await client.PageAsync(path);
        var before = application.RowsOf(user.Id);

        // The one form of the page is the button of the bar at the top, which signs out. Nothing
        // on the page takes anything: no user action switches a rule off, or accepts a plan
        // that was rejected (FR-RSK-3).
        var form = Assert.Single(Form().Matches(page));

        Assert.Contains("action=\"/sign-out\"", form.Value);
        Assert.DoesNotContain("<textarea", page);
        Assert.DoesNotContain("<select", page);
        Assert.DoesNotContain("contenteditable", page);
        Assert.All(Input().Matches(page), input => Assert.Contains("type=\"hidden\"", input.Value));

        // A form that is posted at it all the same is the form of no page, whatever it is
        // called and whatever it brings.
        var token = Browser.AntiforgeryTokenIn(page);

        foreach (var name in new[] { "accept-plan", "override-check", "save-agent" })
        {
            using var posted = await client.SendFormAsync(path, name, token, ("Verdict", "Accepted"), ("Rule", "NoGlobalHalt"), ("Skip", "true"));

            Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
        }

        Assert.Equal(before, application.RowsOf(user.Id));
    }

    /// <summary>The page of the risk checks of an agent.</summary>
    private static string RiskChecksPage(Guid agentId) => $"/agents/{agentId}/risk-checks";

    /// <summary>A cycle of an agent that ended with a plan that went on.</summary>
    private static DecisionCycle Cycle(Guid agentId, Guid userId, DateTimeOffset closedAt, TradingMode mode = TradingMode.Demo) =>
        new(Guid.CreateVersion7(), agentId, userId, mode, closedAt, closedAt.AddSeconds(2), closedAt.AddSeconds(40), CycleOutcome.Approved);

    /// <summary>Gives an agent a cycle for a close, with the one check of a plan, stored as the application stores it.</summary>
    private static async Task GiveCheckAsync(
        AccountsApplication application,
        Guid userId,
        Guid agentId,
        DateTimeOffset closedAt,
        GuardCase checkedCase,
        PlanAction action,
        TradingMode mode = TradingMode.Demo)
    {
        var cycle = Cycle(agentId, userId, closedAt, mode);

        await application.CycleRecords.StoreAsync(
            cycle,
            [],
            [GuardedCycles.Check(cycle.Id, userId, closedAt.AddSeconds(30), checkedCase, action)],
            TestContext.Current.CancellationToken);
    }

    /// <summary>The rules of each check on a page, in the order of the page, each as it is shown.</summary>
    private static IReadOnlyList<IReadOnlyList<ShownRule>> RulesOn(string page) =>
    [
        .. page.Split("<table>").Skip(1).Select(table => (IReadOnlyList<ShownRule>)
        [
            .. Rule().Matches(table).Select(rule => new ShownRule(
                AgentBrowser.Read(rule.Groups[1].Value), AgentBrowser.Read(rule.Groups[2].Value), AgentBrowser.Read(rule.Groups[3].Value))),
        ]),
    ];

    /// <summary>A rule of a check as a page shows it: its name, its outcome, and why.</summary>
    private sealed record ShownRule(string Name, string Outcome, string Why);

    [GeneratedRegex(@"<h2 id=""check-\d+-heading"">([^<]*)</h2>")]
    private static partial Regex CheckHeading();

    [GeneratedRegex(@"<strong class=""guard-verdict"">([^<]*)</strong>")]
    private static partial Regex Verdict();

    [GeneratedRegex(@", in <span class=""mode-badge mode-(demo|live)"">(Demo|Live)</span>, for the")]
    private static partial Regex CheckMode();

    [GeneratedRegex(@"<tr>\s*<th scope=""row"">([^<]*)</th>\s*<td><span class=""rule-outcome rule-[a-z]+"">([^<]*)</span></td>\s*<td>([^<]*)</td>\s*</tr>")]
    private static partial Regex Rule();

    [GeneratedRegex("<form[^>]*>")]
    private static partial Regex Form();

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex Input();
}
