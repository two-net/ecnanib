using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using HttpStatusCode = System.Net.HttpStatusCode;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// UC-04 and EXT-UI-03 through the page: the notices are shown where an agent switches to Live, the switch needs both
/// acknowledgements and the code, and the agent list links to it only for an agent that can switch.
/// </summary>
public sealed class AgentLivePageTests(PostgresFixture postgres)
{
    private const string Form = "agent-live";

    private static CancellationToken Cancellation => AgentApp.Cancellation;

    /// <summary>The notices' bodies, which only the full notices hold. The footer has short forms of each on every page.</summary>
    [Fact]
    public async Task The_page_shows_the_notices_the_Live_keys_and_what_to_acknowledge()
    {
        await using var app = new AgentApp(postgres);
        var owner = await AgentLiveSwitchTests.LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);

        var page = WebUtility.HtmlDecode(await owner.Visitor.PageAsync(Page(agent)));

        Assert.Contains("Switch Scalper to Live", page, StringComparison.Ordinal);
        Assert.Contains("Automated cryptocurrency trading can lose money", page, StringComparison.Ordinal);
        Assert.Contains("Ecnanib is not financial advice, and neither is anything an agent or AI model produces.", page, StringComparison.Ordinal);
        Assert.Contains("Ecnanib is not affiliated with or endorsed by Binance.", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/legal/risk-disclosure\"", page, StringComparison.Ordinal);
        Assert.Matches($"<option value=\"{owner.LiveKey}\"[^>]* selected", page);
        Assert.DoesNotContain(owner.User.ExchangeKey.ToString(), page, StringComparison.Ordinal);
        Assert.Contains("I've read the Risk Disclosure", page, StringComparison.Ordinal);
        Assert.Contains("it asks me before every entry and target change", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.TwoFactorCode", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_acknowledgements_the_page_says_what_to_fix_and_nothing_changes()
    {
        await using var app = new AgentApp(postgres);
        var owner = await AgentLiveSwitchTests.LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);

        using var response = await owner.Visitor.SubmitAsync(Page(agent), Form, ("Input.ExchangeKey", owner.LiveKey.ToString()));
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Not switched to Live. Fix these first:", page, StringComparison.Ordinal);
        Assert.Contains("Confirm that you've read the Risk Disclosure.", page, StringComparison.Ordinal);
        Assert.Contains("Confirm that you understand how this agent sends its trades.", page, StringComparison.Ordinal);
        Assert.True((await app.Agents.FindAsync(owner.User.Id, agent.Id, Cancellation))!.IsDemo);
    }

    [Fact]
    public async Task Both_acknowledgements_switch_the_agent_and_the_list_then_labels_it_Live()
    {
        await using var app = new AgentApp(postgres);
        var owner = await AgentLiveSwitchTests.LiveUserAsync(app);
        using var _ = owner.Visitor;
        var agent = await PausedAsync(app, owner.User);

        using var response = await owner.Visitor.SubmitAsync(Page(agent), Form, Acknowledged(owner));
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancellation));
        var live = (await app.Agents.FindAsync(owner.User.Id, agent.Id, Cancellation))!;

        Assert.Contains("Scalper is now active in Live.", page, StringComparison.Ordinal);
        Assert.Equal((TradingEnvironment.Live, AgentStatus.Active), (live.Settings.Environment, live.Status));

        var list = await owner.Visitor.PageAsync("/agents");

        Assert.Matches(">Live</span>", list);
        Assert.DoesNotContain(Page(agent), list, StringComparison.Ordinal);
    }

    /// <summary>FR-AUTH-05. The code is a secret, so the page never writes it back, even after a refusal.</summary>
    [Fact]
    public async Task With_two_factor_on_the_page_takes_a_code_and_never_writes_it_back()
    {
        await using var app = new AgentApp(postgres);
        var owner = await AgentLiveSwitchTests.LiveUserAsync(app);
        using var _ = owner.Visitor;
        var key = await TwoFactorSteps.StartAsync(owner.Visitor);
        await TwoFactorSteps.EnableAsync(owner.Visitor, key, app.Clock);
        var agent = await PausedAsync(app, owner.User);

        Assert.Contains("name=\"Input.TwoFactorCode\"", await owner.Visitor.PageAsync(Page(agent)), StringComparison.Ordinal);

        using var wrong = await owner.Visitor.SubmitAsync(Page(agent), Form, [.. Acknowledged(owner), ("Input.TwoFactorCode", "never-shown-again")]);
        var refused = WebUtility.HtmlDecode(await wrong.Content.ReadAsStringAsync(Cancellation));

        Assert.Contains("That code didn't work.", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("never-shown-again", refused, StringComparison.Ordinal);
        Assert.True((await app.Agents.FindAsync(owner.User.Id, agent.Id, Cancellation))!.IsDemo);

        using var right = await owner.Visitor.SubmitAsync(Page(agent), Form, [.. Acknowledged(owner), ("Input.TwoFactorCode", app.Clock.NextCode(key))]);

        Assert.Contains("is now active in Live.", WebUtility.HtmlDecode(await right.Content.ReadAsStringAsync(Cancellation)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_links_only_a_paused_or_stopped_Demo_agent_to_the_switch()
    {
        await using var app = new AgentApp(postgres);
        var owner = await AgentLiveSwitchTests.LiveUserAsync(app);
        using var _ = owner.Visitor;
        var paused = await PausedAsync(app, owner.User, "Paused");
        var active = await app.ActiveAsync(owner.User, "Active", Market.Futures);
        var draft = await app.CreateAsync(owner.User.Id, AgentApp.Ready(owner.User, "Draft"));

        var list = await owner.Visitor.PageAsync("/agents");

        Assert.Contains($"href=\"{Page(paused)}\"", list, StringComparison.Ordinal);
        Assert.DoesNotContain(Page(active), list, StringComparison.Ordinal);
        Assert.DoesNotContain(Page(draft), list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_cant_switch_shows_why_without_a_form_and_someone_elses_is_not_found()
    {
        await using var app = new AgentApp(postgres);
        var owner = await AgentLiveSwitchTests.LiveUserAsync(app);
        using var _ = owner.Visitor;
        var active = await app.ActiveAsync(owner.User);

        var page = WebUtility.HtmlDecode(await owner.Visitor.PageAsync(Page(active)));

        Assert.Contains("Only a paused or stopped Demo agent can switch to Live.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Input.AcknowledgeRiskDisclosure", page, StringComparison.Ordinal);

        using var stranger = Visitor.To(app.App);
        await stranger.SignUpAsync();
        using var notFound = await stranger.GetAsync(Page(active));

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.DoesNotContain("Scalper", await notFound.Content.ReadAsStringAsync(Cancellation), StringComparison.Ordinal);
    }

    private static string Page(AgentRecord agent) => $"/agents/{agent.Id}/live";

    /// <summary>The Live key chosen and both boxes ticked, as a browser sends them.</summary>
    private static (string Name, string Value)[] Acknowledged(AgentLiveSwitchTests.LiveUser owner) =>
    [
        ("Input.ExchangeKey", owner.LiveKey.ToString()),
        ("Input.AcknowledgeRiskDisclosure", "true"),
        ("Input.AcknowledgeExecutionMode", "true"),
    ];

    private static async Task<AgentRecord> PausedAsync(AgentApp app, AgentApp.User user, string name = "Scalper")
    {
        var agent = await app.ActiveAsync(user, name);

        return (await app.Agents.PauseAsync(user.Id, agent.Id, Cancellation)).Agent!;
    }
}
