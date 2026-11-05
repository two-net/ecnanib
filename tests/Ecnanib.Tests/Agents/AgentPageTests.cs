using System.Net.Http;
using Ecnanib.Components;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using HttpStatusCode = System.Net.HttpStatusCode;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-AGT-01, FR-AGT-02 and FR-UI-07 through the pages: the settings form creates and edits agents, the list
/// shows them, labelled Demo, and nobody reaches another user's. The list's buttons run on a circuit, which
/// the contract tests cover.
/// </summary>
public sealed class AgentPageTests(PostgresFixture postgres)
{
    private const string ListPage = "/agents";
    private const string NewPage = "/agents/new";
    private const string Form = "agent-settings";

    private static CancellationToken Cancellation => AgentApp.Cancellation;

    [Fact]
    public async Task The_menu_links_to_the_agents_page_which_offers_a_new_agent()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();

        var account = await visitor.PageAsync("/account");
        var list = WebUtility.HtmlDecode(await visitor.PageAsync(ListPage));

        Assert.Contains("href=\"/agents\"", account, StringComparison.Ordinal);
        Assert.Contains("<h1", list, StringComparison.Ordinal);
        Assert.Contains("You haven't created an agent yet.", list, StringComparison.Ordinal);
        Assert.Contains("href=\"/agents/new\"", list, StringComparison.Ordinal);
        Assert.Contains("0 of 10 agents", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_created_through_the_form_is_a_draft_listed_as_Demo()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);

        using var response = await visitor.SubmitAsync(NewPage, Form, Fields(user, ("Input.Name", "Patient <b>trader</b>")));

        Assert.Equal($"{ListPage}?saved=created", Visitor.Target(response));

        var page = WebUtility.HtmlDecode(await visitor.PageAsync($"{ListPage}?saved=created"));
        var agent = Assert.Single(await app.Agents.ListAsync(user.Id, includeArchived: false, Cancellation));

        Assert.Contains("Agent saved as a draft.", page, StringComparison.Ordinal);
        Assert.Contains("Patient <b>trader</b>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>trader", await visitor.PageAsync(ListPage), StringComparison.Ordinal);
        Assert.Contains("Demo", page, StringComparison.Ordinal);
        Assert.Contains("Draft", page, StringComparison.Ordinal);
        Assert.Equal(AgentStatus.Draft, agent.Status);
        Assert.Equal(ExecutionMode.Confirm, agent.Settings.ExecutionMode);
        Assert.Equal(3, agent.Settings.Roles.Count);
        Assert.Equal(250m, agent.Settings.CapitalAllocation);
        Assert.Equal(2, agent.Settings.Risk.MaxLeverage);
    }

    [Fact]
    public async Task A_refused_form_says_what_to_fix_and_saves_nothing()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);

        using var response = await visitor.SubmitAsync(
            NewPage, Form, Fields(user, ("Input.Symbol", "DOGEUSDT"), ("Input.MaxLeverage", "two")));
        var invalid = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Some settings need fixing.", invalid, StringComparison.Ordinal);
        Assert.Contains("Enter a number in this setting's range.", invalid, StringComparison.Ordinal);

        using var unknown = await visitor.SubmitAsync(NewPage, Form, Fields(user, ("Input.Symbol", "DOGEUSDT")));
        var refused = WebUtility.HtmlDecode(await unknown.Content.ReadAsStringAsync(Cancellation));

        Assert.Contains("Binance doesn't list this pair on this market.", refused, StringComparison.Ordinal);
        Assert.Empty(await app.Agents.ListAsync(user.Id, includeArchived: true, Cancellation));
    }

    [Fact]
    public async Task An_agent_is_edited_on_its_settings_page_and_someone_elses_is_not_found()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);
        var agent = await app.CreateAsync(user.Id, AgentApp.Ready(user));
        var page = $"/agents/{agent.Id}/settings";

        var form = WebUtility.HtmlDecode(await visitor.PageAsync(page));
        Assert.Contains("Settings for Scalper", form, StringComparison.Ordinal);
        Assert.Contains("value=\"BTCUSDT\"", form, StringComparison.Ordinal);

        using var response = await visitor.SubmitAsync(page, Form, Fields(user, ("Input.Name", "Renamed")));

        Assert.Equal($"{ListPage}?saved=updated", Visitor.Target(response));
        Assert.Equal("Renamed", (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!.Settings.Name);

        using var stranger = Visitor.To(app.App);
        await stranger.SignUpAsync();
        using var notFound = await stranger.GetAsync(page);
        var body = await notFound.Content.ReadAsStringAsync(Cancellation);

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.DoesNotContain("Renamed", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// FR-ROLE-03: a new agent starts with a default model for each role, from the models the user's keys can
    /// use, and only the ones that can give structured answers are offered (FR-ROLE-02). With one provider for
    /// every role, the page says the Risk Critic shares the Strategist's (FR-ROLE-04).
    /// </summary>
    [Fact]
    public async Task A_new_agent_starts_with_the_default_models_and_only_suitable_ones_are_offered()
    {
        await using var app = new AgentApp(postgres, new()
        {
            ["Catalog:Overrides:0:Provider"] = AgentApp.Provider,
            ["Catalog:Overrides:0:Model"] = "model-b",
            ["Catalog:Overrides:0:StructuredOutput"] = "false",
        });
        app.Ai.On(HttpMethod.Get, CredentialApp.ModelsPath, _ => FakeAiProvider.Models(null, AgentApp.Model, "model-b"));
        using var visitor = Visitor.To(app.App);
        await SignedUpAsync(app, visitor);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync(NewPage));
        var chosen = $"<option value=\"{AgentApp.Provider}|{AgentApp.Model}\" selected";

        Assert.Equal(Enum.GetValues<AiRole>().Length, page.Split(chosen).Length - 1);
        Assert.DoesNotContain($"{AgentApp.Provider}|model-b", page, StringComparison.Ordinal);
        Assert.Contains("The Strategist and the Risk Critic use the same provider.", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_form_shows_the_global_caps_starts_within_them_and_refuses_more()
    {
        await using var app = new AgentApp(postgres, new() { ["RiskCaps:MaxLeverage"] = "1" });
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync(NewPage));

        // The SRS's default of 2 is brought within the cap of 1 (FR-RISK-11), and each capped setting says its cap.
        Assert.Contains("name=\"Input.MaxLeverage\" type=\"text\" inputmode=\"decimal\" value=\"1\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"agent-risk-MaxLeverage-hint agent-risk-MaxLeverage-cap agent-risk-MaxLeverage-error\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"agent-risk-MaxLeverage-cap\"", page, StringComparison.Ordinal);
        Assert.Contains("At most 1 on this site.", page, StringComparison.Ordinal);
        Assert.Contains("At least 1 on this site.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-risk-CooldownCyclesAfterLoss-cap", page, StringComparison.Ordinal);

        using var response = await visitor.SubmitAsync(NewPage, Form, Fields(user, ("Input.MaxLeverage", "2")));
        var refused = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Cancellation));

        Assert.Contains("Some settings need fixing.", refused, StringComparison.Ordinal);
        Assert.Contains("id=\"agent-risk-MaxLeverage\"", refused, StringComparison.Ordinal);
        Assert.Contains("Enter a number in this setting's range.", refused, StringComparison.Ordinal);
        Assert.Empty(await app.Agents.ListAsync(user.Id, includeArchived: true, Cancellation));
    }

    [Fact]
    public async Task An_agent_paused_for_a_loss_limit_says_why_and_when_it_resumes()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);
        var agent = await app.ActiveAsync(user);
        var until = RiskPeriods.NextDay(app.Clock.GetUtcNow());
        await app.Agents.PauseForLossLimitAsync(user.Id, agent.Id, new LossLimitBreach(LossLimitKind.Daily, until), Cancellation);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync(ListPage));

        Assert.Contains("Paused because its losses today reached its daily loss limit.", page, StringComparison.Ordinal);
        Assert.Contains($"Resumes on its own at {DisplayTime.Utc.Format(until)}.", page, StringComparison.Ordinal);
    }

    /// <summary>A user who signed up through the pages, with a valid key of each kind.</summary>
    private static async Task<AgentApp.User> SignedUpAsync(AgentApp app, Visitor visitor)
    {
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        return new AgentApp.User(id, await app.AddBinanceAsync(id), await app.AddAiAsync(id));
    }

    /// <summary>Everything the form posts, as a browser would send it with the defaults, and <paramref name="changes"/> in place.</summary>
    private static (string Name, string Value)[] Fields(AgentApp.User user, params (string Name, string Value)[] changes)
    {
        var model = $"{AgentApp.Provider}|{AgentApp.Model}";
        var fields = new Dictionary<string, string>
        {
            ["Input.Name"] = "Scalper",
            ["Input.Market"] = "Spot",
            ["Input.Symbol"] = "btcusdt",
            ["Input.Timeframe"] = "OneHour",
            ["Input.ExchangeKey"] = user.ExchangeKey.ToString(),
            ["Input.TechnicalAnalyst"] = model,
            ["Input.Strategist"] = model,
            ["Input.RiskCritic"] = model,
            ["Input.CapitalAllocation"] = "250",
            ["Input.MaxRiskPerTradePercent"] = "1",
            ["Input.MaxLeverage"] = "2",
            ["Input.DailyLossLimitPercent"] = "3",
            ["Input.WeeklyLossLimitPercent"] = "6",
            ["Input.ConsecutiveLossesBeforePause"] = "3",
            ["Input.MinRewardToRisk"] = "1.5",
            ["Input.MaxNewEntriesPerDay"] = "4",
            ["Input.MinStopDistanceAtr"] = "0.5",
            ["Input.MaxStopDistanceAtr"] = "4",
            ["Input.LiquidationBufferMultiple"] = "3",
            ["Input.FundingFilterPercent"] = "0.05",
            ["Input.CooldownCyclesAfterLoss"] = "1",
            ["Input.ExecutionMode"] = "Confirm",
            ["Input.AutoExecuteRiskReducing"] = "true",
            ["Input.ExperienceEnabled"] = "true",
            ["Input.ReflectionsInPrompt"] = "5",
        };

        foreach (var (name, value) in changes)
        {
            fields[name] = value;
        }

        return [.. fields.Select(field => (field.Key, field.Value))];
    }
}
