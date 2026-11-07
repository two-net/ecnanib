using Ecnanib.Components.Agents;
using Ecnanib.Components.Approvals;
using Ecnanib.Components.Traces;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Execution;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using HttpStatusCode = System.Net.HttpStatusCode;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// FR-UI-04, FR-UI-01, FR-UI-07 and NFR-SEC-04 through the pages: a cycle's trace shows everything behind it, labelled
/// Demo, with what models wrote only as text and no key anywhere; the agent's page, its last decision and the approval
/// link to it; and nobody reaches another user's. Live refresh runs on a circuit, which LiveUpdatesTests covers.
/// </summary>
public sealed class TracePageTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task A_trace_shows_everything_behind_a_cycle()
    {
        await using var app = new CycleApp(postgres);
        _ = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        app.Replies[AiRole.Strategist] = _ =>
            CycleApp.Reply(CycleApp.Buy.Replace("Momentum above the 20 EMA.", "Momentum <b>above</b> the 20 EMA.", StringComparison.Ordinal));
        using var visitor = Visitor.To(app.Agents.App);
        var (user, agent) = await SignedUpWithAgentAsync(app, visitor, ExecutionMode.Autonomous);
        await app.RunAsync(user, agent);
        var cycle = Assert.Single(await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation));

        var raw = await visitor.PageAsync($"/agents/{agent.Id}/cycles/{cycle.CycleId}");
        var page = WebUtility.HtmlDecode(raw);

        Assert.Contains($"<title>Decision trace | {agent.Settings.Name} | Ecnanib</title>", page, StringComparison.Ordinal);
        Assert.Contains(">Demo</span>", page, StringComparison.Ordinal);
        Assert.Contains("Spot, BTCUSDT, 1H, for the candle that closed", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/agents/{agent.Id}\"", page, StringComparison.Ordinal);

        foreach (var heading in new[]
        {
            "Decision trace", "Summary", "What it saw", "Checks before the council", "Council", "Risk Engine", "Execution and orders",
            "Indicators", "Loss limits", "Rules, in the order they run", "Orders",
        })
        {
            Assert.Contains($">{heading}</h", page, StringComparison.Ordinal);
        }

        foreach (var text in new[]
        {
            AgentText.OutcomeOf(CycleOutcome.Executed), AgentText.RoleOf(AiRole.TechnicalAnalyst), AgentText.RoleOf(AiRole.Strategist),
            AgentText.RoleOf(AiRole.RiskCritic), TraceText.RoleStateOf(RoleState.Done), TraceText.CallStatusOf(CouncilCallStatus.Answered),
            "Instructions", "Message", "Raw answer", "Parsed output", "1,000 in, 200 out", "3,000 in, 600 out",
            TraceText.VerdictOf(RiskVerdict.Pass), TraceText.RuleOf(RiskRule.StopDistance), TraceText.RuleOutcomeOf(RuleOutcome.Pass),
            TraceText.RuleOf(RiskRule.DailyLoss), TraceText.SizedKindOf(SizedOrderKind.Entry),
            "Ask", "EMA 20", "Risk per trade (%)",
            ApprovalText.ExecutionOf(ExecutionStatus.Protected),
            TraceText.LegOf(OrderLeg.Entry), TraceText.LegOf(OrderLeg.Stop), TraceText.LegOf(OrderLeg.Target),
            TraceText.OrderStatusOf(OrderStatus.Filled), TraceText.OrderKindOf(OrderKind.Market),
            "3 orders, oldest first",
        })
        {
            Assert.Contains(text, page, StringComparison.Ordinal);
        }

        // What a model wrote is only text, so its markup arrives encoded.
        Assert.Contains("Momentum &lt;b&gt;above&lt;/b&gt; the 20 EMA.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>above</b>", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", raw.Replace("<style", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // FR-KEY-09: the page carries no key of the user's.
        Assert.DoesNotContain(CredentialApp.AiKey, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ExchangeHarness.ExampleApiKey, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ExchangeHarness.ExampleSecret, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_agents_page_its_last_decision_and_the_approval_link_to_the_trace()
    {
        await using var app = new CycleApp(postgres);
        using var visitor = Visitor.To(app.Agents.App);
        var (user, agent) = await SignedUpWithAgentAsync(app, visitor, ExecutionMode.Confirm);
        await app.RunAsync(user, agent);
        var approval = await app.PendingAsync(user);
        var link = $"href=\"/agents/{agent.Id}/cycles/{approval.CycleId}\"";

        var agentPage = WebUtility.HtmlDecode(await visitor.PageAsync($"/agents/{agent.Id}"));
        var approvalPage = WebUtility.HtmlDecode(await visitor.PageAsync($"/approvals/{approval.Id}"));
        var trace = WebUtility.HtmlDecode(await visitor.PageAsync($"/agents/{agent.Id}/cycles/{approval.CycleId}"));

        Assert.Contains(">Decisions</h2>", agentPage, StringComparison.Ordinal);
        Assert.Contains("The newest 1, each with its own Demo or Live label", agentPage, StringComparison.Ordinal);
        Assert.Contains(AgentText.OutcomeOf(CycleOutcome.AwaitingApproval), agentPage, StringComparison.Ordinal);
        Assert.Contains($">{TraceText.ProposalOf(TradeAction.Buy)}</td>", agentPage, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(agentPage, link));
        Assert.Contains(link, approvalPage, StringComparison.Ordinal);

        // The trace links back to the approval, which hasn't been checked again yet.
        Assert.Contains(">Approval</h2>", trace, StringComparison.Ordinal);
        Assert.Contains($"href=\"/approvals/{approval.Id}\"", trace, StringComparison.Ordinal);
        Assert.Contains("It wasn't checked again.", trace, StringComparison.Ordinal);
        Assert.Contains("What was approved is sent from the approval, above.", trace, StringComparison.Ordinal);
        Assert.Contains("No order cites this cycle.", trace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_over_budget_cycle_shows_its_budget_and_that_it_saw_nothing()
    {
        await using var app = new CycleApp(postgres);
        using var visitor = Visitor.To(app.Agents.App);
        var (user, agent) = await SignedUpWithAgentAsync(app, visitor, ExecutionMode.Confirm, new AiBudget(null, null, 1, null));
        await app.RunAsync(user, agent);
        app.Clock.Advance(TimeSpan.FromHours(1));
        await app.RunAsync(user, agent);
        var cycle = (await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation))[0];

        var page = WebUtility.HtmlDecode(await visitor.PageAsync($"/agents/{agent.Id}/cycles/{cycle.CycleId}"));

        Assert.Contains(AgentText.OutcomeOf(CycleOutcome.OverBudget), page, StringComparison.Ordinal);
        Assert.Contains(">AI budget</h3>", page, StringComparison.Ordinal);
        Assert.Contains("It had reached the daily token limit, until", page, StringComparison.Ordinal);
        Assert.Contains("3,600 of 1", page, StringComparison.Ordinal);
        Assert.Contains("The cycle ended before it read the market and the account.", page, StringComparison.Ordinal);
        Assert.Contains(TraceText.RoleStateOf(RoleState.Skipped), page, StringComparison.Ordinal);
        Assert.Contains("The Risk Engine wasn't asked", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Someone_elses_trace_is_not_found_and_a_visitor_signs_in_first()
    {
        await using var app = new CycleApp(postgres);
        using var visitor = Visitor.To(app.Agents.App);
        var (user, agent) = await SignedUpWithAgentAsync(app, visitor, ExecutionMode.Confirm);
        await app.RunAsync(user, agent);
        var cycle = Assert.Single(await app.Traces.ListRecentAsync(user.Id, agent.Id, 20, Cancellation));
        var page = $"/agents/{agent.Id}/cycles/{cycle.CycleId}";

        using var stranger = Visitor.To(app.Agents.App);
        await stranger.SignUpAsync();
        using var notFound = await stranger.GetAsync(page);
        using var otherAgent = await visitor.GetAsync($"/agents/{Guid.NewGuid()}/cycles/{cycle.CycleId}");
        using var unknown = await visitor.GetAsync($"/agents/{agent.Id}/cycles/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.DoesNotContain("Momentum", await notFound.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, otherAgent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var anonymous = Visitor.To(app.Agents.App);
        using var redirect = await anonymous.GetAsync(page);

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(redirect), StringComparison.Ordinal);
    }

    /// <summary>A user who signed up through the pages, with a key of each kind and an Active agent.</summary>
    private static async Task<(AgentApp.User User, AgentRecord Agent)> SignedUpWithAgentAsync(
        CycleApp app, Visitor visitor, ExecutionMode mode, AiBudget? budget = null)
    {
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var user = new AgentApp.User(id, await app.Agents.AddBinanceAsync(id), await app.Agents.AddAiAsync(id));
        var settings = AgentApp.Ready(user) with { ExecutionMode = mode, Budget = budget ?? AiBudget.None };
        var agent = await app.Agents.CreateAsync(user.Id, settings);
        var activated = await app.Agents.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, activated.Outcome);

        return (user, activated.Agent!);
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
