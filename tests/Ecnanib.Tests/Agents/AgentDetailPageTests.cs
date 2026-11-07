using Ecnanib.Components.Agents;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Execution;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using HttpStatusCode = System.Net.HttpStatusCode;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-UI-01, FR-AGT-11, FR-UI-07 and NFR-SEC-04 through the page: an agent's own page shows its overview, a chart that
/// loads on the circuit, its trades and its actions, labelled Demo, and nobody reaches another user's. What updates
/// live does so on a circuit, which LiveUpdatesTests and the modules' observer tests cover.
/// </summary>
public sealed class AgentDetailPageTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task An_agents_page_shows_its_overview_and_actions_and_the_list_links_to_it()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app.ConnectionString, visitor, app);
        var agent = await app.ActiveAsync(user);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync($"/agents/{agent.Id}"));
        var list = await visitor.PageAsync("/agents");

        Assert.Contains($"href=\"/agents/{agent.Id}\"", list, StringComparison.Ordinal);
        Assert.Contains("<title>Scalper | Ecnanib</title>", page, StringComparison.Ordinal);
        Assert.Contains(">Scalper</h1>", page, StringComparison.Ordinal);
        Assert.Contains(">Demo</span>", page, StringComparison.Ordinal);
        Assert.Contains("Spot, BTCUSDT, 1H, Confirm", page, StringComparison.Ordinal);

        foreach (var fact in new[]
        {
            "Next decision", "About ", "Nothing open", "No closed trades yet", "AI cost to date", "0 US dollars", "Last decision", "None yet",
            "Demo prices resemble Live markets but don't equal them",
        })
        {
            Assert.Contains(fact, page, StringComparison.Ordinal);
        }

        // The chart loads on the circuit, so the first response has its placeholder and its legend.
        Assert.Contains("class=\"skeleton chart-skeleton\"", page, StringComparison.Ordinal);
        Assert.Contains("Loading the chart", page, StringComparison.Ordinal);
        Assert.Contains("Price rose: hollow", page, StringComparison.Ordinal);
        Assert.Contains("No trades yet.", page, StringComparison.Ordinal);

        Assert.Contains(">Pause</button>", page.Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains($"href=\"/agents/{agent.Id}/settings\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", page.Replace("<style", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Someone_elses_agent_is_not_found_and_a_visitor_signs_in_first()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app.ConnectionString, visitor, app);
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user, "Hidden agent"));

        using var stranger = Visitor.To(app.App);
        await stranger.SignUpAsync();
        using var notFound = await stranger.GetAsync($"/agents/{agent.Id}");
        using var unknown = await visitor.GetAsync($"/agents/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.DoesNotContain("Hidden agent", await notFound.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var anonymous = Visitor.To(app.App);
        using var redirect = await anonymous.GetAsync($"/agents/{agent.Id}");

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith("/sign-in", Visitor.Target(redirect), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_shows_the_position_then_the_trade_and_its_result_with_the_way_it_ended()
    {
        await using var app = new ExecutionApp(postgres);
        using var visitor = Visitor.To(app.Agents.App);
        var user = await SignedUpAsync(app.ConnectionString, visitor, app.Agents);
        var created = await app.Agents.CreateAsync(user.Id, AgentApp.Ready(user, market: Market.Futures) with { ExecutionMode = ExecutionMode.Autonomous });
        await app.SetStatusAsync(created.Id, AgentStatus.Active);
        var agent = (await app.Agents.Agents.FindAsync(user.Id, created.Id, Cancellation))!;
        await app.ExecuteAsync(app.Request(user, agent, ExecutionApp.Long(Market.Futures)));
        var placed = app.FuturesLong();

        var open = WebUtility.HtmlDecode(await visitor.PageAsync($"/agents/{agent.Id}"));

        Assert.Contains($"Long {AgentText.Number(placed.Quantity)} BTCUSDT at {AgentText.AmountOf(ExecutionApp.Price, "USDT")}", open, StringComparison.Ordinal);
        Assert.Contains(AgentText.AmountOf(ExecutionApp.Stop, "USDT"), open, StringComparison.Ordinal);
        Assert.Contains(AgentText.AmountOf(ExecutionApp.Target, "USDT"), open, StringComparison.Ordinal);
        Assert.Contains("Unrealized P&L", open, StringComparison.Ordinal);
        Assert.Contains("The newest 1", open, StringComparison.Ordinal);

        app.Book.Trigger(placed.Stop);
        await app.ReconcileAsync();
        await app.JournalAsync();

        var closed = WebUtility.HtmlDecode(await visitor.PageAsync($"/agents/{agent.Id}"));
        var loss = (-200m * placed.Quantity) - (placed.Quantity * (ExecutionApp.Price + ExecutionApp.Stop) * FakeOrderBook.FuturesFee);

        Assert.Contains("Nothing open", closed, StringComparison.Ordinal);
        Assert.Contains("from 1 closed trades, after fees and funding", closed, StringComparison.Ordinal);
        Assert.Contains($"{AgentText.SignedOf(loss)} USDT", closed, StringComparison.Ordinal);
        Assert.Contains(">Loss</span>", closed, StringComparison.Ordinal);
        Assert.Contains(">Stop</td>", closed, StringComparison.Ordinal);

        // The dashboard shows the same overview.
        Assert.Contains($"{AgentText.SignedOf(loss)} USDT", WebUtility.HtmlDecode(await visitor.PageAsync("/dashboard")), StringComparison.Ordinal);
    }

    /// <summary>A user who signed up through the pages, with a valid key of each kind.</summary>
    private static async Task<AgentApp.User> SignedUpAsync(string connectionString, Visitor visitor, AgentApp app)
    {
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(connectionString, email, Cancellation);

        return new AgentApp.User(id, await app.AddBinanceAsync(id), await app.AddAiAsync(id));
    }
}
