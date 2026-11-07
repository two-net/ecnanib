using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.DecisionCycle;
using Ecnanib.Tests.Execution;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using HttpStatusCode = System.Net.HttpStatusCode;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.Ui;

/// <summary>
/// FR-UI-01, FR-AGT-11, FR-APR-01, FR-UI-07 and NFR-SEC-04 through the page: signing in lands on the dashboard, which
/// shows what each agent is doing, labelled Demo, the approvals waiting for the user, and "Stop all agents", and
/// nothing of anyone else's. What updates live does so on a circuit, which LiveUpdatesTests and the modules' observer
/// tests cover.
/// </summary>
public sealed class DashboardPageTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CycleApp.Cancellation;

    [Fact]
    public async Task The_menu_links_to_the_dashboard_which_says_where_to_start()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();

        var page = WebUtility.HtmlDecode(await visitor.PageAsync("/dashboard"));

        Assert.Contains("href=\"/dashboard\"", page, StringComparison.Ordinal);
        Assert.Contains("<title>Dashboard | Ecnanib</title>", page, StringComparison.Ordinal);
        Assert.Contains(">Dashboard</h1>", page, StringComparison.Ordinal);
        Assert.Contains("You have no agents yet.", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/agents/new\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop all agents", page, StringComparison.Ordinal);

        using var anonymous = Visitor.To(app.App);
        using var redirect = await anonymous.GetAsync("/dashboard");

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(redirect), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_agent_shows_what_it_is_doing_and_an_approval_waiting_links_to_its_page()
    {
        await using var app = new CycleApp(postgres);
        _ = new FakeOrderBook(app.Agents.Binance, app.Clock) { Price = CycleApp.Ask };
        using var visitor = Visitor.To(app.Agents.App);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var user = new AgentApp.User(id, await app.Agents.AddBinanceAsync(id), await app.Agents.AddAiAsync(id));
        var agent = await app.Agents.ActiveAsync(user, "Morning scalper");
        await app.RunAsync(user, agent);
        var approval = await app.PendingAsync(user);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync("/dashboard"));

        Assert.Contains($"href=\"/agents/{agent.Id}\"", page, StringComparison.Ordinal);
        Assert.Contains(">Morning scalper</a>", page, StringComparison.Ordinal);
        Assert.Contains(">Demo</span>", page, StringComparison.Ordinal);
        Assert.Contains("Spot, BTCUSDT, 1H", page, StringComparison.Ordinal);
        Assert.Contains("Waiting for you", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/approvals/{approval.Id}\"", page, StringComparison.Ordinal);
        Assert.Contains(">Buy BTCUSDT", page, StringComparison.Ordinal);
        Assert.Contains("Awaiting your approval", page, StringComparison.Ordinal);
        Assert.Contains("BUY: Momentum above the 20 EMA.", page, StringComparison.Ordinal);
        Assert.Contains("About ", page, StringComparison.Ordinal);
        Assert.Contains("AI cost to date", page, StringComparison.Ordinal);
        Assert.Contains("Stop all agents", page, StringComparison.Ordinal);

        using var stranger = Visitor.To(app.Agents.App);
        await stranger.SignUpAsync();
        var theirs = await stranger.PageAsync("/dashboard");

        Assert.DoesNotContain("Morning scalper", theirs, StringComparison.Ordinal);
        Assert.DoesNotContain(approval.Id.ToString(), theirs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_paused_agent_says_it_has_no_next_decision()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        var user = new AgentApp.User(id, await app.AddBinanceAsync(id), await app.AddAiAsync(id));
        var agent = await app.ActiveAsync(user);
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.PauseAsync(user.Id, agent.Id, Cancellation)).Outcome);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync("/dashboard"));

        Assert.Contains("None while it isn't Active", page, StringComparison.Ordinal);
        Assert.Contains("Paused", page, StringComparison.Ordinal);
    }
}
