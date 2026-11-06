using Ecnanib.Components.Approvals;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.Execution;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using HttpStatusCode = System.Net.HttpStatusCode;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.DecisionCycle;

/// <summary>
/// FR-APR-01, FR-APR-02 and FR-UI-07 through the pages: a pending approval shows what the user decides on, labelled
/// Demo, its own page is where notifications link, and nobody reaches another user's. Approving and rejecting run on a
/// circuit, which ApprovalTests covers through the contract.
/// </summary>
public sealed class ApprovalPageTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task A_pending_approval_shows_what_the_user_decides_on()
    {
        await using var app = new CycleApp(postgres);
        _ = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        using var visitor = Visitor.To(app.Agents.App);
        var (user, agent) = await SignedUpWithAgentAsync(app, visitor);
        await app.RunAsync(user, agent);
        var approval = await app.PendingAsync(user);

        var account = await visitor.PageAsync("/account");
        var list = WebUtility.HtmlDecode(await visitor.PageAsync("/approvals"));
        var single = WebUtility.HtmlDecode(await visitor.PageAsync($"/approvals/{approval.Id}"));

        Assert.Contains("href=\"/approvals\"", account, StringComparison.Ordinal);

        foreach (var page in new[] { list, single })
        {
            Assert.Contains("Buy BTCUSDT", page, StringComparison.Ordinal);
            Assert.Contains("Demo", page, StringComparison.Ordinal);
            Assert.Contains("Waiting for you", page, StringComparison.Ordinal);
            Assert.Contains(agent.Settings.Name, page, StringComparison.Ordinal);
            Assert.Contains("Money at risk", page, StringComparison.Ordinal);
            Assert.Contains(ApprovalText.AmountOf(approval.Proposal.MoneyAtRisk!.Value, "USDT"), page, StringComparison.Ordinal);
            Assert.Contains($"At market, about {ApprovalText.AmountOf(CycleApp.Ask, "USDT")}", page, StringComparison.Ordinal);
            Assert.Contains(ApprovalText.AmountOf(60118m, "USDT"), page, StringComparison.Ordinal);
            Assert.Contains(ApprovalText.AmountOf(60135m, "USDT"), page, StringComparison.Ordinal);
            Assert.Contains("Momentum above the 20 EMA.", page, StringComparison.Ordinal);
            Assert.Contains("Approved: Sound.", page, StringComparison.Ordinal);
            Assert.Contains(">Approve</button>", page, StringComparison.Ordinal);
            Assert.Contains(">Reject</button>", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_decided_approval_says_how_it_went()
    {
        await using var app = new CycleApp(postgres);
        _ = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        using var visitor = Visitor.To(app.Agents.App);
        var (user, agent) = await SignedUpWithAgentAsync(app, visitor);
        await app.RunAsync(user, agent);
        var approval = await app.PendingAsync(user);
        await app.Approvals.RejectAsync(user.Id, approval.Id, Cancellation);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync($"/approvals/{approval.Id}"));

        Assert.Contains("Rejected. Nothing was sent.", page, StringComparison.Ordinal);
        Assert.DoesNotContain(">Approve</button>", page, StringComparison.Ordinal);
        Assert.Contains("Nothing is waiting for your approval.", WebUtility.HtmlDecode(await visitor.PageAsync("/approvals")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Someone_elses_approval_is_not_found_and_a_visitor_signs_in_first()
    {
        await using var app = new CycleApp(postgres);
        using var visitor = Visitor.To(app.Agents.App);
        var (user, agent) = await SignedUpWithAgentAsync(app, visitor);
        await app.RunAsync(user, agent);
        var approval = await app.PendingAsync(user);
        var page = $"/approvals/{approval.Id}";

        using var stranger = Visitor.To(app.Agents.App);
        await stranger.SignUpAsync();
        using var notFound = await stranger.GetAsync(page);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.DoesNotContain("Momentum", await notFound.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.DoesNotContain("Momentum", await stranger.PageAsync("/approvals"), StringComparison.Ordinal);

        using var anonymous = Visitor.To(app.Agents.App);
        using var redirect = await anonymous.GetAsync(page);

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(redirect), StringComparison.Ordinal);
    }

    /// <summary>A user who signed up through the pages, with a key of each kind and an Active Confirm agent.</summary>
    private static async Task<(AgentApp.User User, AgentRecord Agent)> SignedUpWithAgentAsync(CycleApp app, Visitor visitor)
    {
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var user = new AgentApp.User(id, await app.Agents.AddBinanceAsync(id), await app.Agents.AddAiAsync(id));
        var agent = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user));
        var activated = await app.Agents.Agents.ActivateAsync(user.Id, agent.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, activated.Outcome);

        return (user, activated.Agent!);
    }
}
