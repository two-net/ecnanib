using System.Net.Http;
using System.Text.RegularExpressions;
using Ecnanib.Components;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;
using Ecnanib.Modules.Scheduler.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using HttpStatusCode = System.Net.HttpStatusCode;
using WebUtility = System.Net.WebUtility;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-UI-02, NFR-USA-03 and UC-01 through the wizard at /agents/new. Each post sends what a browser would from the
/// step as shown, its hidden fields included, so the settings it carries between steps are tested as they travel.
/// </summary>
public sealed partial class AgentWizardTests(PostgresFixture postgres)
{
    private const string NewPage = "/agents/new";

    private static CancellationToken Cancellation => AgentApp.Cancellation;

    /// <summary>
    /// UC-01: Futures, Demo, BTCUSDT, 1H, the suggested roles, the default risk settings and Autonomous, then the agent is
    /// active and the page says when its first cycle is.
    /// </summary>
    [Fact]
    public async Task A_first_agent_starts_in_Demo_from_the_wizards_suggestions()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);
        var wizard = await Wizard.OpenAsync(visitor);

        await wizard.PressAsync("Next", ("Input.Market", "Futures"));
        Assert.Equal("Pair", wizard.Step);

        await wizard.PressAsync("Next", ("Input.Symbol", " btcusdt "));
        Assert.Equal("Roles", wizard.Step);

        await wizard.PressAsync("Next");
        Assert.Equal("Risk", wizard.Step);
        Assert.Contains("Capital (USDT)", wizard.Text, StringComparison.Ordinal);

        await wizard.PressAsync("Next", ("Input.CapitalAllocation", "100"));
        Assert.Equal("Execution", wizard.Step);

        var review = await wizard.PressAsync("Next", ("Input.ExecutionMode", "Autonomous"));
        Assert.Equal("Review", wizard.Step);
        Assert.Contains("value=\"BTCUSDT 1H\"", review, StringComparison.Ordinal);
        Assert.Contains("If its stops are hit, the agent loses at most 1 USDT on a trade. It pauses after losing 3 USDT in a day, or 6 USDT in a week.", review, StringComparison.Ordinal);

        var cycles = app.App.Services.GetRequiredService<ICycleTimes>();
        var before = DisplayTime.Utc.Format(cycles.NextCycleAt(Timeframe.OneHour));
        var started = await wizard.PressAsync("Start");
        var after = DisplayTime.Utc.Format(cycles.NextCycleAt(Timeframe.OneHour));

        Assert.Equal(HttpStatusCode.OK, wizard.Status);
        Assert.Contains("Your agent is running", started, StringComparison.Ordinal);
        Assert.Contains("Demo", started, StringComparison.Ordinal);
        Assert.True(
            started.Contains($"BTCUSDT 1H is running in Demo. Its first decision is due at about {before}, when the next 1H candle closes.", StringComparison.Ordinal)
            || started.Contains($"Its first decision is due at about {after},", StringComparison.Ordinal),
            started);

        var agent = Assert.Single(await app.Agents.ListAsync(user.Id, includeArchived: false, Cancellation));
        var defaults = (await app.Agents.ListRoleChoicesAsync(user.Id, Cancellation)).Defaults;

        Assert.Equal(AgentStatus.Active, agent.Status);
        Assert.Equal(TradingEnvironment.Demo, agent.Settings.Environment);
        Assert.Equal(Market.Futures, agent.Settings.Market);
        Assert.Equal("BTCUSDT", agent.Settings.Symbol);
        Assert.Equal(Timeframe.OneHour, agent.Settings.Timeframe);
        Assert.Equal(ExecutionMode.Autonomous, agent.Settings.ExecutionMode);
        Assert.Equal(user.ExchangeKey, agent.Settings.ExchangeCredentialId);
        Assert.Equal(100m, agent.Settings.CapitalAllocation);
        Assert.Equal(RiskSettings.Default, agent.Settings.Risk);
        Assert.Equal(defaults.OrderBy(role => role.Role), agent.Settings.Roles.OrderBy(role => role.Role));
        Assert.True(agent.Settings.AutoExecuteRiskReducing);
        Assert.Equal(ExperienceMemory.Default, agent.Settings.Experience);
    }

    /// <summary>
    /// Each step shows its own settings and carries the rest in hidden fields, never both, or a setting would post twice.
    /// On Spot, the risk step carries leverage, the liquidation buffer and the funding filter unseen.
    /// </summary>
    [Fact]
    public async Task Each_step_shows_its_own_settings_and_carries_the_rest()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        await SignedUpAsync(app, visitor);
        var wizard = await Wizard.OpenAsync(visitor);

        (string Name, string Value)[][] steps =
        [
            [],
            [("Input.Symbol", "BTCUSDT")],
            [],
            [("Input.CapitalAllocation", "250")],
            [],
        ];

        foreach (var (step, changes) in new[] { "Market", "Pair", "Roles", "Risk", "Execution" }.Zip(steps))
        {
            Assert.Equal(step, wizard.Step);
            AssertCarriedOnce(wizard.Form);
            await wizard.PressAsync("Next", changes);
        }

        Assert.Equal("Review", wizard.Step);
        AssertCarriedOnce(wizard.Form);

        await wizard.PressAsync("Risk");
        Assert.Contains("type=\"hidden\" name=\"Input.MaxLeverage\" value=\"2\"", wizard.Form, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"agent-risk-MaxLeverage\"", wizard.Form, StringComparison.Ordinal);

        // NFR-USA-03: every setting the step shows has its help, which its field names.
        foreach (var setting in Enum.GetValues<RiskSetting>().Where(setting => !IsFuturesOnly(setting)))
        {
            Assert.Contains($"aria-describedby=\"agent-risk-{setting}-hint", wizard.Form, StringComparison.Ordinal);
            Assert.Contains($"id=\"agent-risk-{setting}-hint\"", wizard.Form, StringComparison.Ordinal);
        }

        Assert.Contains("aria-describedby=\"agent-capital-hint agent-capital-error\"", wizard.Form, StringComparison.Ordinal);
    }

    /// <summary>FR-UI-02: the first step explains the markets and the Futures terms, and that a new agent is in Demo.</summary>
    [Fact]
    public async Task The_first_step_explains_the_markets_and_suggests_a_working_Demo_key()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);

        var page = WebUtility.HtmlDecode(await visitor.PageAsync(NewPage));

        Assert.Contains("<h1", page, StringComparison.Ordinal);
        Assert.Contains("Step 1 of 6", page, StringComparison.Ordinal);
        Assert.Matches("aria-current=\"step\"[^>]*>Market and environment</li>", page);
        Assert.Contains("It gains only when the price rises, and can't lose more than it spent.", page, StringComparison.Ordinal);
        Assert.Contains("It can gain when the price falls as well as when it rises, and can use leverage", page, StringComparison.Ordinal);

        foreach (var term in new[] { "Going short", "Leverage", "Liquidation", "Funding" })
        {
            Assert.Contains($">{term}</dt>", page, StringComparison.Ordinal);
        }

        Assert.Contains("Every agent starts in Demo, which trades on Binance's demo servers with test funds", page, StringComparison.Ordinal);
        Assert.Contains($"<option value=\"{user.ExchangeKey}\" selected", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Before you start", page, StringComparison.Ordinal);
    }

    /// <summary>NFR-USA-02: someone without keys is told what to add, and adding one brings them back.</summary>
    [Fact]
    public async Task Someone_without_keys_is_told_what_to_add_and_comes_back_after_adding_one()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        await visitor.SignUpAsync();
        const string AddPage = "/credentials/add?returnUrl=%2Fagents%2Fnew";

        var page = WebUtility.HtmlDecode(await visitor.PageAsync(NewPage));

        Assert.Contains("Before you start", page, StringComparison.Ordinal);
        Assert.Contains("You have no Binance Demo key yet.", page, StringComparison.Ordinal);
        Assert.Contains("None of your AI keys can fill a role yet.", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"{AddPage}\"", page, StringComparison.Ordinal);

        using var added = await visitor.SubmitAsync(
            AddPage, "add-ai-key", ("Input.Provider", CredentialApp.Provider), ("Input.Label", "Personal"), ("Input.ApiKey", CredentialApp.AiKey));

        Assert.Equal(NewPage, Visitor.Target(added));

        using var elsewhere = await visitor.SubmitAsync(
            "/credentials/add?returnUrl=https%3A%2F%2Fexample.com%2F", "add-ai-key",
            ("Input.Provider", AgentApp.Second), ("Input.Label", "Other"), ("Input.ApiKey", CredentialApp.AiKey));

        Assert.Equal("/credentials?saved=added", Visitor.Target(elsewhere));
    }

    /// <summary>FR-AGT-02: the pairs offered are those Binance's Demo trades, and only perpetual contracts on Futures.</summary>
    [Fact]
    public async Task The_pairs_offered_are_those_an_agent_can_trade()
    {
        await using var app = new AgentApp(postgres, beforeStart: started => started.Binance.On(
            HttpMethod.Get, "/fapi/v1/exchangeInfo", _ => FakeBinance.Json(FuturesWithDelivery)));
        using var visitor = Visitor.To(app.App);
        await SignedUpAsync(app, visitor);

        var spot = await Wizard.OpenAsync(visitor);
        await spot.PressAsync("Next");

        Assert.Contains("<option value=\"BTCUSDT\"", spot.Form, StringComparison.Ordinal);
        Assert.DoesNotContain("ETHBTC", spot.Form, StringComparison.Ordinal);

        var futures = await Wizard.OpenAsync(visitor);
        await futures.PressAsync("Next", ("Input.Market", "Futures"));

        Assert.Contains("<option value=\"BTCUSDT\"", futures.Form, StringComparison.Ordinal);
        Assert.DoesNotContain("BTCUSDM26", futures.Form, StringComparison.Ordinal);

        await futures.PressAsync("Next", ("Input.Symbol", "BTCUSDM26"));

        Assert.Equal("Pair", futures.Step);
        Assert.Contains("Choose a pair from the list", futures.Text, StringComparison.Ordinal);
    }

    /// <summary>A step is left only when its settings are right, and Back leaves without checking.</summary>
    [Fact]
    public async Task A_step_is_left_only_when_its_settings_are_right()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        await SignedUpAsync(app, visitor);
        var wizard = await Wizard.OpenAsync(visitor);
        await wizard.PressAsync("Next");

        await wizard.PressAsync("Next", ("Input.Symbol", "DOGEUSDT"));

        Assert.Equal("Pair", wizard.Step);
        Assert.Contains("Some settings need fixing.", wizard.Text, StringComparison.Ordinal);
        Assert.Contains("Choose a pair from the list: one that Binance's Demo trades on this market", wizard.Text, StringComparison.Ordinal);
        Assert.Contains("aria-invalid=\"true\" aria-describedby=\"agent-symbol-hint agent-symbol-error\"", wizard.Form, StringComparison.Ordinal);

        await wizard.PressAsync("Next", ("Input.Symbol", "BTC-USDT"));
        Assert.Contains("Enter a symbol of letters and digits, such as BTCUSDT.", wizard.Text, StringComparison.Ordinal);

        await wizard.PressAsync("Back");

        Assert.Equal("Market", wizard.Step);
        Assert.DoesNotContain("Some settings need fixing.", wizard.Text, StringComparison.Ordinal);
        Assert.Contains("type=\"hidden\" name=\"Input.Symbol\" value=\"BTC-USDT\"", wizard.Form, StringComparison.Ordinal);
    }

    /// <summary>
    /// FR-RISK-11 and NFR-USA-03 on the risk step: each capped setting says its cap, the defaults start within the caps,
    /// and a setting beyond its cap keeps the user on the step.
    /// </summary>
    [Fact]
    public async Task The_risk_step_shows_the_global_caps_starts_within_them_and_refuses_more()
    {
        await using var app = new AgentApp(postgres, new() { ["RiskCaps:MaxLeverage"] = "1" });
        using var visitor = Visitor.To(app.App);
        await SignedUpAsync(app, visitor);
        var wizard = await Wizard.OpenAsync(visitor);
        await wizard.PressAsync("Next", ("Input.Market", "Futures"));
        await wizard.PressAsync("Next", ("Input.Symbol", "BTCUSDT"));
        await wizard.PressAsync("Next");

        var page = wizard.Text;

        Assert.Equal("Risk", wizard.Step);
        Assert.Contains("name=\"Input.MaxLeverage\" type=\"text\" inputmode=\"decimal\" value=\"1\"", page, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"agent-risk-MaxLeverage-hint agent-risk-MaxLeverage-cap agent-risk-MaxLeverage-error\"", page, StringComparison.Ordinal);
        Assert.Contains("At most 1 on this site.", page, StringComparison.Ordinal);
        Assert.Contains("At least 1 on this site.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-risk-CooldownCyclesAfterLoss-cap", page, StringComparison.Ordinal);
        Assert.Contains(">Liquidation</dt>", page, StringComparison.Ordinal);

        await wizard.PressAsync("Next", ("Input.CapitalAllocation", "100"), ("Input.MaxLeverage", "2"));

        Assert.Equal("Risk", wizard.Step);
        Assert.Contains("Enter a number in this setting's range.", wizard.Text, StringComparison.Ordinal);
        Assert.Contains("id=\"agent-risk-MaxLeverage\" name=\"Input.MaxLeverage\" type=\"text\" inputmode=\"decimal\" value=\"2\" autocomplete=\"off\" aria-invalid=\"true\"", wizard.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FR-ROLE-02 to FR-ROLE-04: each role starts with the suggested model, only models that can give structured answers
    /// are offered, and with one provider for every role, the page says the Risk Critic shares the Strategist's.
    /// </summary>
    [Fact]
    public async Task The_roles_step_starts_with_the_suggestions_and_offers_only_suitable_models()
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
        var wizard = await Wizard.OpenAsync(visitor);
        await wizard.PressAsync("Next");
        await wizard.PressAsync("Next", ("Input.Symbol", "BTCUSDT"));

        var page = wizard.Text;
        var chosen = $"<option value=\"{AgentApp.Provider}|{AgentApp.Model}\" selected";

        Assert.Equal("Roles", wizard.Step);
        Assert.Equal(Enum.GetValues<AiRole>().Length, page.Split(chosen).Length - 1);
        Assert.DoesNotContain($"{AgentApp.Provider}|model-b", page, StringComparison.Ordinal);
        Assert.Contains("The Strategist and the Risk Critic use the same provider.", page, StringComparison.Ordinal);
        Assert.Contains("Challenges each proposed trade, and can reject or tighten it.", page, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"agent-role-Strategist-hint agent-roles-error\"", page, StringComparison.Ordinal);
    }

    /// <summary>An agent that can't start yet is saved as a draft, and the page says what to fix and where.</summary>
    [Fact]
    public async Task Starting_without_a_key_saves_a_draft_and_says_what_to_fix()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);

        var review = await WalkToReviewAsync(visitor, ("Input.ExchangeKey", ""));

        Assert.Contains("Not chosen. The agent can't start without one.", review.Text, StringComparison.Ordinal);

        var result = await review.Wizard.PressAsync("Start");
        var agent = Assert.Single(await app.Agents.ListAsync(user.Id, includeArchived: false, Cancellation));

        Assert.Equal(AgentStatus.Draft, agent.Status);
        Assert.Contains("Saved, but not started", result, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT 1H is saved as a draft. Before it can start:", result, StringComparison.Ordinal);
        Assert.Contains(AgentText(ActivationProblemKind.NoExchangeKey), result, StringComparison.Ordinal);
        Assert.Contains($"href=\"/agents/{agent.Id}/settings\"", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// FR-AGT-01 and FR-UI-07: a draft saved from the wizard is listed as Demo, and the name the user chose is only text.
    /// </summary>
    [Fact]
    public async Task A_draft_saved_from_the_wizard_is_listed_as_Demo()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);

        var review = await WalkToReviewAsync(visitor);
        await review.Wizard.PressAsync("Draft", ("Input.Name", "Patient <b>trader</b>"));

        Assert.Equal("/agents?saved=created", Visitor.Target(review.Wizard.Response!));

        var list = WebUtility.HtmlDecode(await visitor.PageAsync("/agents?saved=created"));
        var agent = Assert.Single(await app.Agents.ListAsync(user.Id, includeArchived: false, Cancellation));

        Assert.Contains("Agent saved as a draft.", list, StringComparison.Ordinal);
        Assert.Contains("Patient <b>trader</b>", list, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>trader", await visitor.PageAsync("/agents"), StringComparison.Ordinal);
        Assert.Contains("Demo", list, StringComparison.Ordinal);
        Assert.Equal(AgentStatus.Draft, agent.Status);
        Assert.Equal(ExecutionMode.Confirm, agent.Settings.ExecutionMode);
    }

    /// <summary>
    /// The suggested name is free, and a name the user makes clash keeps them on Review with nothing created. Review's
    /// Change goes back to a step, and from there straight back to Review.
    /// </summary>
    [Fact]
    public async Task Review_suggests_a_free_name_refuses_a_taken_one_and_changes_a_step()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);
        await app.CreateAsync(user.Id, AgentApp.Ready(user, name: "BTCUSDT 1H"));

        var review = await WalkToReviewAsync(visitor);

        Assert.Contains("value=\"BTCUSDT 1H (2)\"", review.Text, StringComparison.Ordinal);

        var wizard = review.Wizard;
        await wizard.PressAsync("Start", ("Input.Name", "btcusdt 1h"));

        Assert.Equal("Review", wizard.Step);
        Assert.Contains("You already have an agent with this name.", wizard.Text, StringComparison.Ordinal);
        Assert.Single(await app.Agents.ListAsync(user.Id, includeArchived: true, Cancellation));

        await wizard.PressAsync("Pair");

        Assert.Equal("Pair", wizard.Step);
        Assert.Contains("Back to review", wizard.Text, StringComparison.Ordinal);

        await wizard.PressAsync("ToReview", ("Input.Timeframe", "FourHours"));

        Assert.Equal("Review", wizard.Step);
        Assert.Contains(">4H</dd>", wizard.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every setting travels in the page, so the last step checks them all again: a changed hidden field goes back to its
    /// own step, and another user's key is refused (NFR-SEC-04).
    /// </summary>
    [Fact]
    public async Task A_changed_hidden_setting_is_caught_on_its_own_step()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var user = await SignedUpAsync(app, visitor);
        var stranger = await app.UserAsync();

        var (wizard, _) = await WalkToReviewAsync(visitor);

        await wizard.PressAsync("Start", ("Input.WeeklyLossLimitPercent", "a lot"));

        Assert.Equal("Risk", wizard.Step);
        Assert.Contains("Enter a number in this setting's range.", wizard.Text, StringComparison.Ordinal);

        await wizard.PressAsync("ToReview", ("Input.WeeklyLossLimitPercent", "6"));
        await wizard.PressAsync("Start", ("Input.ExchangeKey", stranger.ExchangeKey.ToString()));

        Assert.Equal("Market", wizard.Step);
        Assert.Contains("Choose one of your Binance Demo keys.", wizard.Text, StringComparison.Ordinal);
        Assert.Empty(await app.Agents.ListAsync(user.Id, includeArchived: true, Cancellation));
    }

    /// <summary>A Demo key that Binance doesn't allow on Futures is refused on the first step, rather than at activation.</summary>
    [Fact]
    public async Task A_key_that_cant_trade_the_market_is_refused_on_the_first_step()
    {
        await using var app = new AgentApp(postgres);
        using var visitor = Visitor.To(app.App);
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);
        app.BinanceAccepts(futures: false);
        var key = await app.AddBinanceAsync(id, label: "Spot only");
        await app.AddAiAsync(id);

        var wizard = await Wizard.OpenAsync(visitor);

        Assert.Contains("Spot only (doesn't allow USDⓈ-M Futures)", wizard.Text, StringComparison.Ordinal);

        await wizard.PressAsync("Next", ("Input.Market", "Futures"), ("Input.ExchangeKey", key.ToString()));

        Assert.Equal("Market", wizard.Step);
        Assert.Contains("This key doesn't allow USDⓈ-M Futures on Binance.", wizard.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Futures exchange list with a delivery contract beside the perpetual one. Binance names those with an underscore,
    /// which the pair's shape refuses anyway, so this one has a name that passes, to reach the list.
    /// </summary>
    private static readonly string FuturesWithDelivery = BinanceSamples.FuturesExchangeInfo.Replace(
        "\"symbols\":[",
        """
        "symbols":[{"symbol":"BTCUSDM26","pair":"BTCUSDT","contractType":"CURRENT_QUARTER","status":"TRADING","baseAsset":"BTC","quoteAsset":"USDT",
           "marginAsset":"USDT","pricePrecision":1,"quantityPrecision":3,"orderTypes":["LIMIT","MARKET"],"filters":[]},
        """,
        StringComparison.Ordinal);

    private static bool IsFuturesOnly(RiskSetting setting) =>
        setting is RiskSetting.MaxLeverage or RiskSetting.LiquidationBufferMultiple or RiskSetting.FundingFilterPercent;

    private static string AgentText(ActivationProblemKind kind) =>
        Ecnanib.Components.Agents.AgentText.ProblemOf(new ActivationProblem(kind));

    /// <summary>Through every step to Review on Spot, BTCUSDT and 1H with 250 of capital, and the changes on the first.</summary>
    private static async Task<(Wizard Wizard, string Text)> WalkToReviewAsync(Visitor visitor, params (string Name, string Value)[] first)
    {
        var wizard = await Wizard.OpenAsync(visitor);
        await wizard.PressAsync("Next", first);
        await wizard.PressAsync("Next", ("Input.Symbol", "BTCUSDT"));
        await wizard.PressAsync("Next");
        await wizard.PressAsync("Next", ("Input.CapitalAllocation", "250"));
        var text = await wizard.PressAsync("Next");

        Assert.Equal("Review", wizard.Step);

        return (wizard, text);
    }

    /// <summary>A setting is either shown on the step or carried hidden, never both, so it never posts twice.</summary>
    private static void AssertCarriedOnce(string form)
    {
        var hidden = HiddenName().Matches(form).Select(match => match.Groups["name"].Value).ToList();
        var shown = ShownName().Matches(form).Select(match => match.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(hidden.Count, hidden.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(hidden.Intersect(shown, StringComparer.Ordinal));
    }

    /// <summary>A user who signed up through the pages, with a valid key of each kind.</summary>
    private static async Task<AgentApp.User> SignedUpAsync(AgentApp app, Visitor visitor)
    {
        var email = await visitor.SignUpAsync();
        var id = await AccountRows.IdAsync(app.ConnectionString, email, Cancellation);

        return new AgentApp.User(id, await app.AddBinanceAsync(id), await app.AddAiAsync(id));
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"(?<name>Input\\.[^\"]+)\"")]
    private static partial Regex HiddenName();

    [GeneratedRegex("<(?:input|select|textarea)\\b(?![^>]*type=\"hidden\")[^>]*\\bname=\"(?<name>Input\\.[^\"]+)\"")]
    private static partial Regex ShownName();

    /// <summary>The wizard in a browser: each press posts what the step's form holds as shown, with changes in place.</summary>
    private sealed partial class Wizard(Visitor visitor)
    {
        public string Page { get; private set; } = "";

        public HttpResponseMessage? Response { get; private set; }

        public HttpStatusCode? Status => Response?.StatusCode;

        public string Text => WebUtility.HtmlDecode(Page);

        /// <summary>The wizard's form, as served.</summary>
        public string Form => Forms().Matches(Page).Select(match => match.Value).Single(form => form.Contains("value=\"agent-wizard\"", StringComparison.Ordinal));

        /// <summary>The step the page shows, by name.</summary>
        public string Step => StepField().Match(Form).Groups["step"].Value;

        public static async Task<Wizard> OpenAsync(Visitor visitor)
        {
            var wizard = new Wizard(visitor) { Page = await visitor.PageAsync(NewPage) };

            return wizard;
        }

        /// <summary>Presses the button that posts <paramref name="go"/>, and returns the page after, decoded.</summary>
        public async Task<string> PressAsync(string go, params (string Name, string Value)[] changes)
        {
            var values = Values(Form);

            foreach (var (name, value) in changes)
            {
                values[name] = value;
            }

            values["Wizard.Go"] = go;

            using var content = new FormUrlEncodedContent(values);
            Response?.Dispose();
            Response = await visitor.Client.PostAsync(NewPage, content, Cancellation);
            Page = await Response.Content.ReadAsStringAsync(Cancellation);

            return Text;
        }

        /// <summary>What a browser posts from the form: its inputs, ticked boxes and radios, chosen options and text areas.</summary>
        private static Dictionary<string, string> Values(string form)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (Match input in Inputs().Matches(form))
            {
                var attributes = Attributes(input.Value);

                if (attributes.GetValueOrDefault("name") is not { } name
                    || (attributes.GetValueOrDefault("type") is "radio" or "checkbox" && !attributes.ContainsKey("checked")))
                {
                    continue;
                }

                values[name] = attributes.GetValueOrDefault("value") ?? "";
            }

            foreach (Match select in Selects().Matches(form))
            {
                var options = Options().Matches(select.Groups["options"].Value).Select(option => Attributes(option.Value)).ToList();
                var chosen = options.FirstOrDefault(option => option.ContainsKey("selected")) ?? options.FirstOrDefault();
                values[WebUtility.HtmlDecode(select.Groups["name"].Value)] = chosen?.GetValueOrDefault("value") ?? "";
            }

            foreach (Match area in TextAreas().Matches(form))
            {
                values[WebUtility.HtmlDecode(area.Groups["name"].Value)] = WebUtility.HtmlDecode(area.Groups["text"].Value);
            }

            return values;
        }

        private static Dictionary<string, string?> Attributes(string tag) =>
            Attribute().Matches(tag).ToDictionary(
                match => match.Groups["name"].Value,
                match => match.Groups["value"].Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null,
                StringComparer.Ordinal);

        [GeneratedRegex("<form\\b.*?</form>", RegexOptions.Singleline)]
        private static partial Regex Forms();

        [GeneratedRegex("name=\"Wizard\\.Step\" value=\"(?<step>[^\"]*)\"")]
        private static partial Regex StepField();

        [GeneratedRegex("<input\\b[^>]*>")]
        private static partial Regex Inputs();

        [GeneratedRegex("""<select\b[^>]*\bname="(?<name>[^"]*)"[^>]*>(?<options>.*?)</select>""", RegexOptions.Singleline)]
        private static partial Regex Selects();

        [GeneratedRegex("<option\\b[^>]*>")]
        private static partial Regex Options();

        [GeneratedRegex("""<textarea\b[^>]*\bname="(?<name>[^"]*)"[^>]*>(?<text>.*?)</textarea>""", RegexOptions.Singleline)]
        private static partial Regex TextAreas();

        [GeneratedRegex("""\s(?<name>[\w:.-]+)(?:="(?<value>[^"]*)")?""")]
        private static partial Regex Attribute();
    }
}
