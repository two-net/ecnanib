using System.Net;
using System.Text.RegularExpressions;
using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.WebUi.Pages.Agents;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// A user can run a test cycle for an agent that produces a full decision record and places no
/// orders (FR-CYC-12): each agent has a page that says what a test cycle is and runs one, and
/// the pages of its cycles and of its risk checks tell the record of a test cycle apart from
/// those of the cycles that a candle close started. See "A test cycle" in docs/cycles.md.
/// </summary>
/// <remarks>
/// The cycles call their models through the gateway itself, with the stored keys of their users
/// and with the test in the place of the providers, and are recorded in the database, as
/// <see cref="TeamRecordTests"/> runs them. The exchange has all the market data.
/// </remarks>
public partial class TestCyclePageTests
{
    private const string Ada = "ada@example.com";

    /// <summary>A moment between two candle closes: seven and a half minutes after the close of 14:00.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 14, 7, 30, TimeSpan.Zero);

    /// <summary>The latest close of a candle of an hour then.</summary>
    private static readonly DateTimeOffset LastHour = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);

    /// <summary>The team of the agents: each role at a provider of its own, as a new agent starts.</summary>
    private static readonly IReadOnlyDictionary<Role, string> Team = new Dictionary<Role, string>
    {
        [Role.MarketAnalyst] = "gpt",
        [Role.SentimentAnalyst] = "grok",
        [Role.Strategist] = "gemini",
        [Role.RiskManager] = "claude",
    };

    [Fact]
    public async Task The_page_says_what_a_test_cycle_is_and_runs_none_until_its_button_is_pressed()
    {
        var providers = new CountedProviders();
        using var cycles = Application(providers);
        var (client, user, agent) = await SignedInAsync(cycles);

        var page = await client.PageAsync(AgentBrowser.TestCyclePage(agent));
        var read = AgentBrowser.Read(page);

        Assert.Contains("<title>Test cycle of the agent Swing</title>", page);
        Assert.Contains("<h1>Test cycle of the agent Swing</h1>", page);

        // What a test cycle is, that it places no order, what it costs, how long it takes, and
        // what its stale limit counts from, in plain language (NFR-USA-2).
        Assert.Contains(CyclePages.TestExplained, read);
        Assert.Contains($"<strong>{CyclePages.TestPlacesNoOrder}</strong>", read);
        Assert.Contains(CyclePages.TestCosts, read);
        Assert.Contains(CyclePages.TestTakes, read);
        Assert.Contains(CyclePages.TestStaleLimit, read);
        Assert.Contains("The agent is <strong>Draft</strong>, and a test cycle does not start it.", read);

        // One form of its own, with one button, and the token against request forgery.
        Assert.Single(Form().Matches(page), form => form.Value.Contains(AgentBrowser.TestCyclePage(agent), StringComparison.Ordinal));
        Assert.Contains("<button type=\"submit\">Run a test cycle</button>", page);
        Assert.Contains("name=\"__RequestVerificationToken\"", page);
        Assert.Contains($"name=\"_handler\" value=\"{AgentBrowser.TestCycleForm}\"", page);

        // The page of the state of the agent and the list of its cycles lead to it.
        var link = $"<a href=\"{AgentBrowser.TestCyclePage(agent)}\">Run a test cycle</a>";

        Assert.Contains(link, await client.PageAsync(AgentBrowser.AgentStatePage(agent)));
        Assert.Contains(link, await client.PageAsync(CyclesPage(agent)));

        // Reading the pages runs nothing: no model was called, and nothing is recorded.
        Assert.Equal(0, providers.Calls);
        Assert.Empty(cycles.Application.RowsOf(user)["decision_cycles"]);
    }

    [Fact]
    public async Task Running_a_test_cycle_leads_to_its_timeline_which_says_that_it_is_a_test_and_placed_no_orders()
    {
        var providers = new CountedProviders();
        using var cycles = Application(providers);
        var (client, user, agent) = await SignedInAsync(cycles);

        // A cycle that the close of an hour ago started, for the pages to tell the two apart.
        var scheduled = await cycles.Application.GiveCycleAsync(user, agent, LastHour.AddHours(-1), orders: false);

        var (path, page) = await client.RunTestCycleAsync(agent);
        var read = AgentBrowser.Read(page);

        // The cycle ran, with a call of each role, and is recorded as a test cycle. The page
        // waited for it, and leads to its timeline.
        var cycle = await TestCycleAsync(cycles);

        Assert.Equal((agent, user, LastHour, CycleOutcome.Approved), (cycle.AgentId, cycle.UserId, cycle.ClosedAt, cycle.Outcome));
        Assert.Equal(4, providers.Calls);
        Assert.Equal(CyclePage(agent, cycle.Id), path);
        Assert.Contains("<title>Cycle of the agent Swing</title>", page);

        // The timeline says that it is a test cycle, and that it placed no orders.
        Assert.Contains($"<p class=\"cycle-kind cycle-kind-test\"><strong>Test cycle.</strong> {CyclePages.TestCycleRan}</p>", read);

        // The record is full: the data package, every role output, the Risk Guard result, and
        // the final outcome (FR-CYC-10). Where its orders would stand, it says that it placed none.
        Assert.Equal(
            [
                "The candle closed",
                "The test cycle started",
                "Data package",
                "Market Analyst",
                "Sentiment Analyst",
                "Strategist",
                "Risk Manager",
                "Risk Guard: Open a long position",
                "No order",
                "The test cycle ended: Approved",
            ],
            Heading().Matches(read).Select(heading => heading.Groups[1].Value));
        Assert.Contains(CyclePages.TestStarted, read);
        Assert.Contains("It was the latest closed candle of the agent's timeframe when the test cycle was run.", read);
        Assert.Equal(
            "A test cycle places no order. The Risk Guard accepted the plan: in a cycle that a candle close starts, it would have " +
            "gone on to the orders.",
            Assert.Single(NoOrder().Matches(read)).Groups[1].Value);

        // The candle closed minutes before the cycle was run: what happened in the cycle says
        // how long after its start that was.
        var times = Times().Matches(read).Select(time => time.Groups[1].Value).ToList();

        Assert.Equal("18 November 2026, 14:00:00 UTC, as the candle closed", times[0]);
        Assert.All(times.Skip(1), time => Assert.Equal("18 November 2026, 14:07:30 UTC, as the test cycle started", time));

        // The stale limit of the check counts from the moment the cycle was run.
        Assert.Contains("The plan was checked 0 seconds after the test cycle was run. The stale limit of the agent's timeframe is 200 seconds.", read);
        Assert.Contains("Every rule that this plan is held to holds, so it would go on to the orders. The check is of a test cycle, which places no order.", read);

        // It placed no orders: nothing of an order or a position is stored for the agent.
        Assert.Empty(cycles.Application.RowsOf(user)["orders"]);
        Assert.Empty(cycles.Application.RowsOf(user)["positions"]);

        // The list tells it apart from the cycle that a candle close started, latest close first.
        var list = await client.PageAsync(CyclesPage(agent));

        Assert.Equal(["Test", "Scheduled"], Kind().Matches(list).Select(kind => kind.Groups[1].Value));
        Assert.Contains(CyclePages.TestsListed, AgentBrowser.Read(list));
        Assert.Contains($"<a href=\"{CyclePage(agent, cycle.Id)}\">Timeline</a>", list);

        // The timeline of that other cycle says nothing of a test.
        var other = AgentBrowser.Read(await client.PageAsync(CyclePage(agent, scheduled)));

        Assert.DoesNotContain("Test cycle.", other);
        Assert.Contains("The cycle started", Heading().Matches(other).Select(heading => heading.Groups[1].Value));
        Assert.DoesNotContain("test cycle", other);

        // The page of the risk checks says of which cycle each check is.
        var checks = AgentBrowser.Read(await client.PageAsync($"/agents/{agent}/risk-checks"));

        Assert.Contains("candle that closed at 18 November 2026, 14:00 UTC, in a test cycle.", checks);
        Assert.Contains("The plan was checked 0 seconds after the test cycle was run.", checks);
        Assert.Contains("candle that closed at 18 November 2026, 13:00 UTC.", checks);
        Assert.Contains("so it goes on to the orders.", checks);

        // The agent is as it was, and its page of the state leads on.
        Assert.Equal("Draft", StoredAgent.Of(cycles.Application.Database, user).Single().State);
    }

    [Fact]
    public async Task A_test_cycle_that_fails_is_recorded_and_shown_as_one_that_failed()
    {
        // The agent trades on Futures, and the exchange does not say the mark price: the data
        // package of its cycle cannot be built.
        var providers = new CountedProviders();
        using var cycles = Application(providers);
        var (client, _, agent) = await SignedInAsync(cycles, market: Market.Futures);

        cycles.Market!.MarkPriceOutcome = ExchangeCallOutcome.TimedOut;

        var (path, page) = await client.RunTestCycleAsync(agent);
        var read = AgentBrowser.Read(page);

        // The cycle is recorded, as every cycle is, and the page leads to its timeline.
        var cycle = await TestCycleAsync(cycles);

        Assert.Equal(CycleOutcome.Failed, cycle.Outcome);
        Assert.Equal(CyclePage(agent, cycle.Id), path);
        Assert.Equal(
            ["The candle closed", "The test cycle started", "Data package", "No order", "The test cycle ended: Failed"],
            Heading().Matches(read).Select(heading => heading.Groups[1].Value));
        Assert.Equal(
            "A test cycle places no order. Nothing of a plan went on to the Risk Guard either.",
            Assert.Single(NoOrder().Matches(read)).Groups[1].Value);
        Assert.Equal(0, providers.Calls);
    }

    [Fact]
    public async Task A_team_that_cannot_work_is_said_on_the_page_and_no_test_cycle_is_run()
    {
        var providers = new CountedProviders();
        using var cycles = Application(providers);
        var (client, user, agent) = await SignedInAsync(cycles, team: false);

        // Two roles of four have a model.
        await cycles.Application.GiveRoleAsync(user, agent, Role.MarketAnalyst, Now.AddHours(-1), "gpt", "gpt-test-model");
        await cycles.Application.GiveRoleAsync(user, agent, Role.SentimentAnalyst, Now.AddHours(-1), "grok", "grok-test-model");

        var (path, page) = await client.RunTestCycleAsync(agent);
        var read = AgentBrowser.Read(page);

        // The page says why, names each role that stands in the way, and leads to the settings.
        Assert.Equal(AgentBrowser.TestCyclePage(agent), path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{CyclePages.TestRolesBlock}</p>", read);
        Assert.Contains($"<li>{AgentPages.Says(new RoleBlock(Role.Strategist, RoleProblem.Missing))}</li>", read);
        Assert.Contains($"<li>{AgentPages.Says(new RoleBlock(Role.RiskManager, RoleProblem.Missing))}</li>", read);
        Assert.Contains($"<a href=\"{AgentBrowser.AgentPage(agent)}\">Settings of the agent</a>", page);

        // The form is there to try again, once the team is whole.
        Assert.Contains("<button type=\"submit\">Run a test cycle</button>", page);

        // No model is paid for a cycle that cannot be completed, and nothing is recorded.
        Assert.Equal(0, providers.Calls);
        Assert.Empty(cycles.Application.RowsOf(user)["decision_cycles"]);
    }

    [Fact]
    public async Task The_page_of_an_agent_in_Live_says_that_nothing_is_placed_in_the_real_account()
    {
        var providers = new CountedProviders();
        using var cycles = Application(providers);
        var (client, _, live) = await SignedInAsync(cycles, mode: TradingMode.Live, state: AgentState.Stopped);

        var read = AgentBrowser.Read(await client.PageAsync(AgentBrowser.TestCyclePage(live)));

        // A test cycle affects no trading, so the page asks nothing once more (FR-UI-3). It
        // says what the mode means for it, under the badge of the mode.
        Assert.Contains(AgentBrowser.Badge(TradingMode.Live), read);
        Assert.Contains("The agent is in Live. A test cycle places no order in your real Binance account either.", read);
        Assert.Contains("<button type=\"submit\">Run a test cycle</button>", read);
    }

    /// <summary>
    /// The application with the agents of the database, whose cycles call their models through
    /// the gateway itself and are recorded in the database. The file of the tests allows the
    /// models of Claude alone: here it allows those of every provider of the team, so that a
    /// user can select them, as a team that is started must be one that its user can select.
    /// </summary>
    private static CyclesApplication Application(CountedProviders providers) =>
        new(
            Now,
            providers: providers.System,
            settings: file => file
                .WithJson("AiProviders:Gemini:Allow", """[ "gemini-*" ]""")
                .WithJson("AiProviders:GPT:Allow", """[ "gpt-*" ]""")
                .WithJson("AiProviders:Grok:Allow", """[ "grok-*" ]"""));

    /// <summary>
    /// Gives a new user a Binance key for Demo and an agent named Swing, with a team of four
    /// providers that the keys of the user can use unless the test says that it has none, and a
    /// browser that the user is signed in on.
    /// </summary>
    private static async Task<(HttpClient Client, Guid UserId, Guid AgentId)> SignedInAsync(
        CyclesApplication cycles,
        Market market = Market.Spot,
        TradingMode mode = TradingMode.Demo,
        AgentState state = AgentState.Draft,
        bool team = true)
    {
        var user = await cycles.Application.RegisterVerifiedAsync(Ada);
        await cycles.Application.GiveExchangeKeyAsync(user.Id, Now.AddHours(-1));

        var agent = await cycles.Application.GiveAgentAsync(
            user.Id, Now.AddHours(-1), name: "Swing", state: state, market: market, mode: mode, team: false);

        if (team)
        {
            foreach (var (role, provider) in Team)
            {
                await cycles.Application.GiveRoleAsync(user.Id, agent, role, Now.AddHours(-1), provider, $"{provider}-test-model");
            }
        }

        var client = cycles.Application.CreateClient();
        await client.SignInAsync(Ada);

        return (client, user.Id, agent);
    }

    /// <summary>The one test cycle that is recorded.</summary>
    private static async Task<DecisionCycle> TestCycleAsync(CyclesApplication cycles)
    {
        await using var context = await cycles.Application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken);

        return await context.Set<DecisionCycle>()
            .AsNoTracking()
            .SingleAsync(cycle => cycle.Kind == CycleKind.Test, TestContext.Current.CancellationToken);
    }

    private static string CyclesPage(Guid agentId) => $"/agents/{agentId}/cycles";

    private static string CyclePage(Guid agentId, Guid cycleId) => $"/agents/{agentId}/cycles/{cycleId}";

    /// <summary>
    /// Stands for the AI providers: it answers each role with a valid result of its schema, as
    /// the provider of that role answers, and counts the calls.
    /// </summary>
    private sealed class CountedProviders
    {
        private int _calls;

        public CountedProviders() =>
            System = new StandInSystem(async (request, cancellation) =>
            {
                Interlocked.Increment(ref _calls);

                var provider = ModelCalls.ProviderOf(request);
                var role = Team.Single(given => given.Value == provider).Key;
                var offered = (await request.Content!.ReadAsStringAsync(cancellation)).Contains("\"tools\"", StringComparison.Ordinal);
                var answered = TeamAnswers.Of(role);

                return StandInSystem.Json(HttpStatusCode.OK, offered ? ModelCalls.Searched(provider, answered) : ModelCalls.Finished(provider, answered));
            });

        public StandInSystem System { get; }

        /// <summary>How many calls of a model were made.</summary>
        public int Calls => Volatile.Read(ref _calls);
    }

    [GeneratedRegex(@"<h2[^>]*>([^<]*)</h2>")]
    private static partial Regex Heading();

    [GeneratedRegex(@"<p class=""timeline-time"">([^<]*)</p>")]
    private static partial Regex Times();

    [GeneratedRegex(@"<p class=""no-order"">([^<]*)</p>")]
    private static partial Regex NoOrder();

    [GeneratedRegex(@"<span class=""cycle-kind cycle-kind-[a-z]+"">([^<]*)</span>")]
    private static partial Regex Kind();

    [GeneratedRegex("<form[^>]*>")]
    private static partial Regex Form();
}
