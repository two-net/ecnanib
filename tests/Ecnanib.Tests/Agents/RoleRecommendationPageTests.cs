using Ecnanib.Agents;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Experience;
using Ecnanib.WebUi.Pages.Agents;
using Ecnanib.WebUi.Pages.Experience;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// Role by strength: the form of an agent shows the strongest candidate for each role, from the
/// strength score of each model in each role, and lets the user apply the recommended team in
/// one action or override any role (FR-TEAM-4). Slices without enough data are not recommended
/// (FR-EXP-4). A recommendation uses Live data where there is enough, otherwise Demo data, and
/// says which was used (FR-EXP-9). See "Role by strength" in docs/agents.md.
/// </summary>
/// <remarks>
/// The application of these tests damps by 4 and takes 4 samples as enough, so that a slice of
/// 4 samples has half of its measure as its score. The file of the tests allows the models of
/// Claude only.
/// </remarks>
public class RoleRecommendationPageTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string TeamModel = StoredAgentRows.TeamModel;
    private const string Stronger = "claude-b";

    private const string NotEnough = AgentPages.NotEnoughDataToRecommend;

    private static readonly DateTimeOffset Morning = new(2026, 11, 17, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_form_shows_the_strongest_candidate_of_each_role_with_its_score_its_sample_and_which_data_was_used()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (agent, _) = await MeasuredAsync(application, user.Id);

        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        // The Market Analyst: the other model was right 4 times of 4, which is 50 points above
        // a half, and 4 samples of 8 give half of it. The model that the role has was right 3
        // times of 4. The Strategist: the other model made 1 R on average over 4 plans, and
        // the model that the role has made no plan.
        Assert.Equal(
            "Strongest candidate: claude-b, of Claude. Strength score +25 percentage points, sample size 4, Demo data. " +
            "Recommended: the model that the role has scores +12.5 percentage points (sample size 4, Demo data).",
            AgentBrowser.CandidateOn(form, "market-analyst"));
        Assert.Equal(
            "Strongest candidate: claude-b, of Claude. Strength score +0.5 R, sample size 4, Demo data. " +
            "Recommended: the model that the role has has no strength score yet, which counts as 0.",
            AgentBrowser.CandidateOn(form, "strategist"));

        // Nothing is measured for the Sentiment Analyst. And the Risk Manager approved 4 plans
        // and vetoed none: it has a sample, and no measure, so no score (FR-EXP-4).
        Assert.Equal(NotEnough, AgentBrowser.CandidateOn(form, "sentiment-analyst"));
        Assert.Equal(NotEnough, AgentBrowser.CandidateOn(form, "risk-manager"));

        // Showing it stores nothing, and the form holds the models that the roles have.
        Assert.Equal(TeamModel, AgentBrowser.ModelChosenOn(form, "market-analyst"));
        Assert.All(StoredAgentRole.Of(application.Database, agent), role => Assert.Equal(TeamModel, role.Model));
    }

    [Fact]
    public async Task The_form_shows_the_formula_and_states_what_the_figures_cannot_tell()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");

        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        // What a candidate is, the formula with the damping of the file, and from how many
        // samples on a slice has a score (FR-EXP-4).
        Assert.Contains($"<p>{AgentPages.StrengthExplained}</p>", form);
        Assert.Contains("Strength score = measure × n ÷ (n + 4), where n is the sample size.", form);
        Assert.Contains(ExperiencePages.EnoughExplained(4), form);

        // The statements of the experience screens (FR-EXP-11).
        Assert.Contains(ExperiencePages.PastResults, form);
        Assert.Contains(ExperiencePages.SmallSamples(4), form);
        Assert.Contains(ExperiencePages.DemoDiffers, form);

        // And where every score is: the experience screen, for the market and the timeframe of the agent.
        Assert.Contains($"<a href=\"/experience?market=spot&timeframe=1H\">{AgentPages.StrengthsLink}</a>", form);
    }

    [Fact]
    public async Task The_recommended_team_is_saved_with_one_press_and_a_role_with_no_recommendation_keeps_what_the_form_holds()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (agent, team) = await MeasuredAsync(application, user.Id);
        var third = await application.GiveModelAsync("claude", "claude-c", Morning);
        await application.LetKeyUseAsync(user.Id, "claude", "claude-c", Morning);

        // The form has the button, beside the one that saves it as it is.
        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        Assert.Contains("<button type=\"submit\">Save the agent</button>", form);
        Assert.Contains("<button type=\"submit\" name=\"Apply\" value=\"recommended\">Save with the recommended team</button>", form);

        // One press (FR-TEAM-4): the form is as the roles are stored, but for the Sentiment
        // Analyst, which she gives a model of her own choice. The Market Analyst and the
        // Strategist are given their recommended model, whatever the form held for them. The
        // Sentiment Analyst is recommended none, and has what she chose. The Risk Manager is as it was.
        var (path, _) = await client.SaveAgentAsync(
            agent,
            [.. AgentBrowser.Entry(name: "Swing").WithTeam("claude", team).WithRole("SentimentAnalyst", "claude", third), .. AgentBrowser.RecommendedOn(form)]);

        Assert.Equal(AgentBrowser.AgentsPage, path);
        Assert.Equal(
            ["MarketAnalyst: claude-b", "SentimentAnalyst: claude-c", "Strategist: claude-b", "RiskManager: claude-test-model"],
            StoredAgentRole.Told(application.Database, agent));

        // Each of those two roles has its strongest candidate now, and the form says so. No
        // role is recommended another model, so the button is gone.
        var after = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        Assert.EndsWith("Demo data. The role has it.", AgentBrowser.CandidateOn(after, "market-analyst"));
        Assert.EndsWith("Demo data. The role has it.", AgentBrowser.CandidateOn(after, "strategist"));
        Assert.DoesNotContain("name=\"Apply\"", after);
        Assert.Contains($"<p role=\"note\">{AgentPages.NothingRecommended}</p>", after);
    }

    [Fact]
    public async Task Saving_the_recommended_team_of_an_agent_in_Live_asks_once_more_with_the_recommended_models_in_the_form()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (agent, team) = await MeasuredAsync(application, user.Id, AgentState.Running, TradingMode.Live);
        var stronger = await application.GiveModelAsync("claude", Stronger, Morning);
        var stored = StoredAgentRole.Told(application.Database, agent);

        // What the agent did in Live is Live data, and the line says so, as a word: the badge
        // of the page is the mode of the agent, and there is one.
        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        Assert.StartsWith(
            "Strongest candidate: claude-b, of Claude. Strength score +25 percentage points, sample size 4, Live data.",
            AgentBrowser.CandidateOn(form, "market-analyst"));

        // The models of the roles of an agent in Live are a change to what it does with real
        // money: nothing is stored, and the form asks once more, with the recommended models in
        // it (FR-UI-3).
        var entry = AgentBrowser.Entry(name: "Swing").WithTeam("claude", team);
        var (path, asks) = await client.SaveAgentAsync(agent, [.. entry, .. AgentBrowser.RecommendedOn(form)]);

        Assert.Equal(AgentBrowser.AgentPage(agent), path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.ConfirmSaveForLive}</p>", AgentBrowser.Read(asks));
        Assert.Equal(
            ("claude-b", "claude-test-model", "claude-b", "claude-test-model"),
            (AgentBrowser.ModelChosenOn(asks, "market-analyst"),
                AgentBrowser.ModelChosenOn(asks, "sentiment-analyst"),
                AgentBrowser.ModelChosenOn(asks, "strategist"),
                AgentBrowser.ModelChosenOn(asks, "risk-manager")));
        Assert.Contains("<button type=\"submit\" name=\"Confirm\" value=\"live\">Save for Live</button>", asks);
        Assert.DoesNotContain("name=\"Apply\"", asks);
        Assert.DoesNotContain("name=\"Recommended\"", asks);
        Assert.Equal(stored, StoredAgentRole.Told(application.Database, agent));

        // The button that says so once more stores the form as it came back. The agent runs on.
        var (saved, _) = await client.SaveAgentAsync(
            agent,
            [.. entry.WithRole("MarketAnalyst", "claude", stronger).WithRole("Strategist", "claude", stronger), ("Confirm", "live")]);

        Assert.Equal(AgentBrowser.AgentsPage, saved);
        Assert.Equal(
            ["MarketAnalyst: claude-b", "SentimentAnalyst: claude-test-model", "Strategist: claude-b", "RiskManager: claude-test-model"],
            StoredAgentRole.Told(application.Database, agent));
        Assert.Equal("Running", StoredAgent.Named(application.Database, "Swing").State);
    }

    [Fact]
    public async Task A_user_overrides_a_role_by_choosing_another_model_and_saving_as_before()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (agent, team) = await MeasuredAsync(application, user.Id);
        var third = await application.GiveModelAsync("claude", "claude-c", Morning);
        await application.LetKeyUseAsync(user.Id, "claude", "claude-c", Morning);

        // The Market Analyst is recommended another model. She gives it a third, which nothing
        // is measured of, and saves the form with its own button: it is hers to decide.
        var (path, _) = await client.SaveAgentAsync(
            agent, AgentBrowser.Entry(name: "Swing").WithTeam("claude", team).WithRole("MarketAnalyst", "claude", third));

        Assert.Equal(AgentBrowser.AgentsPage, path);
        Assert.Equal(
            ["MarketAnalyst: claude-c", "SentimentAnalyst: claude-test-model", "Strategist: claude-test-model", "RiskManager: claude-test-model"],
            StoredAgentRole.Told(application.Database, agent));

        // The form says what the role has now, and recommends its strongest candidate still.
        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        Assert.Equal("claude-c", AgentBrowser.ModelChosenOn(form, "market-analyst"));
        Assert.EndsWith(
            "Recommended: the model that the role has has no strength score yet, which counts as 0.",
            AgentBrowser.CandidateOn(form, "market-analyst"));
    }

    [Fact]
    public async Task The_form_that_creates_an_agent_shows_the_candidates_once_a_timeframe_is_chosen_and_creates_it_with_the_recommended_team()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await MeasuredAsync(application, user.Id);

        // A strength score is of one market and one timeframe, and a new agent has no
        // timeframe yet: the form says how to see the candidates, and shows none.
        var first = AgentBrowser.Read(await client.PageAsync(AgentBrowser.NewAgentPage));

        Assert.Contains($"<p role=\"note\">{AgentPages.CandidatesNeedTimeframe}</p>", first);
        Assert.Null(AgentBrowser.CandidateOn(first, "market-analyst"));
        Assert.DoesNotContain("name=\"Apply\"", first);

        // The button that shows them stores nothing. A new agent has no model in any role, so a
        // candidate that scores above 0 is recommended.
        var entry = AgentBrowser.Entry(name: "Fresh", symbol: "SOLUSDT");
        var (shownAt, shown) = await client.CreateAgentAsync([.. entry, AgentBrowser.ShowCandidates]);
        var read = AgentBrowser.Read(shown);

        Assert.Equal(AgentBrowser.NewAgentPage, shownAt);
        Assert.Equal(
            "Strongest candidate: claude-b, of Claude. Strength score +25 percentage points, sample size 4, Demo data. " +
            "Recommended: the role has no model yet.",
            AgentBrowser.CandidateOn(read, "market-analyst"));
        Assert.Equal(NotEnough, AgentBrowser.CandidateOn(read, "risk-manager"));
        Assert.Contains("<button type=\"submit\" name=\"Apply\" value=\"recommended\">Create with the recommended team</button>", read);
        Assert.Single(StoredAgent.All(application.Database));

        // One press creates the agent with the recommended model in each role that has one. The
        // other roles wait for theirs, as on any new agent.
        var (path, _) = await client.CreateAgentAsync([.. entry, .. AgentBrowser.RecommendedOn(read)]);

        Assert.Equal(AgentBrowser.AgentsPage, path);
        Assert.Equal(
            ["MarketAnalyst: claude-b", "Strategist: claude-b"],
            StoredAgentRole.Told(application.Database, StoredAgent.Named(application.Database, "Fresh").Id));
    }

    [Fact]
    public async Task The_candidates_are_those_of_the_market_and_the_timeframe_that_the_form_holds()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await MeasuredAsync(application, user.Id);

        // Everything was measured on Spot, on the hour. On 4 hours nothing is: no role has a
        // candidate, and no button recommends anything (FR-EXP-4).
        var entry = AgentBrowser.Entry(name: "Fresh", symbol: "SOLUSDT", timeframe: "4H");
        var (_, shown) = await client.CreateAgentAsync([.. entry, AgentBrowser.ShowCandidates]);
        var read = AgentBrowser.Read(shown);

        Assert.All(
            new[] { "market-analyst", "sentiment-analyst", "strategist", "risk-manager" },
            role => Assert.Equal(NotEnough, AgentBrowser.CandidateOn(read, role)));
        Assert.DoesNotContain("name=\"Apply\"", read);
        Assert.Contains($"<p role=\"note\">{AgentPages.NothingRecommended}</p>", read);
        Assert.Contains($"<a href=\"/experience?market=spot&timeframe=4H\">{AgentPages.StrengthsLink}</a>", read);

        // And on Futures, on the hour, nothing is either.
        var (_, onFutures) = await client.CreateAgentAsync(
            [.. AgentBrowser.Entry(name: "Fresh", market: "Futures", symbol: "SOLUSDT"), AgentBrowser.ShowCandidates]);

        Assert.Equal(NotEnough, AgentBrowser.CandidateOn(onFutures, "market-analyst"));
        Assert.DoesNotContain(StoredAgent.All(application.Database), stored => stored.Name == "Fresh");
    }

    [Fact]
    public async Task The_button_stores_the_team_that_the_form_showed_and_nothing_when_another_is_recommended_by_then()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (agent, team) = await MeasuredAsync(application, user.Id);
        var entry = AgentBrowser.Entry(name: "Swing").WithTeam("claude", team);

        // The form recommends the second model for the Market Analyst and for the Strategist.
        var shown = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        Assert.StartsWith("Strongest candidate: claude-b, of Claude.", AgentBrowser.CandidateOn(shown, "market-analyst"));

        // Before she presses the button, a third model comes to score highest as the Market
        // Analyst: right 12 times of 12, of which 12 samples of 16 give three quarters.
        var third = await application.GiveModelAsync("claude", "claude-c", Morning);
        await application.LetKeyUseAsync(user.Id, "claude", "claude-c", Morning);
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, third, Morning.AddHours(-3), matched: 12, missed: 0);

        // The button saves what the form showed, and nothing that she did not see: nothing is
        // stored, and the form is back with what is recommended now.
        var (path, back) = await client.SaveAgentAsync(agent, [.. entry, .. AgentBrowser.RecommendedOn(shown)]);
        var read = AgentBrowser.Read(back);

        Assert.Equal(AgentBrowser.AgentPage(agent), path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.RecommendationChanged}</p>", read);
        Assert.StartsWith(
            "Strongest candidate: claude-c, of Claude. Strength score +37.5 percentage points, sample size 12, Demo data.",
            AgentBrowser.CandidateOn(read, "market-analyst"));
        Assert.Equal(TeamModel, AgentBrowser.ModelChosenOn(read, "market-analyst"));
        Assert.All(StoredAgentRole.Of(application.Database, agent), role => Assert.Equal(TeamModel, role.Model));

        // A form that names another timeframe than the one that the candidates were shown for
        // stores nothing either, and neither does one that brings the button with nothing that
        // a page recommended.
        var (other, _) = await client.SaveAgentAsync(agent, [.. entry.With("Timeframe", "4H"), .. AgentBrowser.RecommendedOn(read)]);
        var (bare, _) = await client.SaveAgentAsync(agent, [.. entry, AgentBrowser.Recommended]);

        Assert.Equal((AgentBrowser.AgentPage(agent), AgentBrowser.AgentPage(agent)), (other, bare));
        Assert.All(StoredAgentRole.Of(application.Database, agent), role => Assert.Equal(TeamModel, role.Model));
        Assert.Equal("OneHour", StoredAgent.Named(application.Database, "Swing").Timeframe);

        // Pressed on the form as it came back, the button saves what that form shows.
        var (saved, _) = await client.SaveAgentAsync(agent, [.. entry, .. AgentBrowser.RecommendedOn(read)]);

        Assert.Equal(AgentBrowser.AgentsPage, saved);
        Assert.Equal(
            ["MarketAnalyst: claude-c", "SentimentAnalyst: claude-test-model", "Strategist: claude-b", "RiskManager: claude-test-model"],
            StoredAgentRole.Told(application.Database, agent));
    }

    [Fact]
    public async Task The_form_warns_before_a_recommended_team_that_is_reduced_is_saved()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (agent, _) = await MeasuredAsync(application, user.Id);

        // The Strategist has a model of Gemini, and the three other roles one of Claude.
        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, provider: "gemini", model: "gemini-a");

        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        // The team as it is: Claude holds three roles, and the Risk Manager reviews the plan of
        // another provider.
        Assert.Equal([AgentPages.ReducedTeam(["Claude"])], AgentBrowser.WarningsOn(form));

        // Strength gives the Strategist a model of Claude, which the Risk Manager has too. The
        // form warns of the team that the button would save, where the button is, before it is
        // saved (FR-TEAM-5).
        Assert.StartsWith("Strongest candidate: claude-b, of Claude.", AgentBrowser.CandidateOn(form, "strategist"));
        Assert.Equal(
            [
                AgentPages.OfRecommendedTeam(AgentPages.ReducedTeam(["Claude"])),
                AgentPages.OfRecommendedTeam(AgentPages.SameProviderReviews("Claude")),
            ],
            AgentBrowser.RecommendedWarningsOn(form));
    }

    [Fact]
    public async Task What_another_users_agents_did_recommends_nothing()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await client.SignInAsync(Ada);

        // Grace's agents measured a model. Ada's key can use the same model, and her agents
        // measured nothing.
        await MeasuredAsync(application, grace.Id);
        var agent = await application.GiveAgentAsync(ada.Id, Morning.AddDays(-2), name: "Swing");
        await application.LetKeyUseAsync(ada.Id, "claude", Stronger, Morning);

        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        // Nothing of what the agents of anyone else did is in a recommendation (NFR-SEC-3).
        Assert.All(
            new[] { "market-analyst", "sentiment-analyst", "strategist", "risk-manager" },
            role => Assert.Equal(NotEnough, AgentBrowser.CandidateOn(form, role)));
        Assert.DoesNotContain("name=\"Apply\"", form);

        // And a form that is posted with the button all the same changes no role, and stores nothing.
        var team = Assert.Single(StoredAgentRole.Of(application.Database, agent).Select(role => role.ModelId).Distinct());
        var (path, _) = await client.SaveAgentAsync(agent, [.. AgentBrowser.Entry(name: "Renamed").WithTeam("claude", team), AgentBrowser.Recommended]);

        Assert.Equal(AgentBrowser.AgentPage(agent), path);
        Assert.All(StoredAgentRole.Of(application.Database, agent), role => Assert.Equal(TeamModel, role.Model));
        Assert.Equal("Swing", Assert.Single(StoredAgent.Of(application.Database, ada.Id)).Name);
    }

    [Fact]
    public async Task A_model_that_the_users_key_cannot_use_is_no_candidate()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var (agent, team) = await MeasuredAsync(application, user.Id, keyUsesStronger: false);

        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        // The model that scores highest as the Market Analyst is one that her key cannot use,
        // so the strongest that the role can be given is the model that it has. The Strategist
        // has no candidate at all (FR-CAT-9).
        Assert.Equal(
            "Strongest candidate: claude-test-model, of Claude. Strength score +12.5 percentage points, sample size 4, Demo data. The role has it.",
            AgentBrowser.CandidateOn(form, "market-analyst"));
        Assert.Equal(NotEnough, AgentBrowser.CandidateOn(form, "strategist"));

        // A form that is posted with the button all the same gives no role that model.
        await client.SaveAgentAsync(agent, [.. AgentBrowser.Entry(name: "Swing").WithTeam("claude", team), AgentBrowser.Recommended]);

        Assert.All(StoredAgentRole.Of(application.Database, agent), role => Assert.Equal(TeamModel, role.Model));
    }

    [Fact]
    public async Task A_candidate_that_does_not_score_above_the_model_that_the_role_has_is_shown_and_not_recommended()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");
        var stronger = await application.GiveModelAsync("claude", Stronger, Morning);
        await application.LetKeyUseAsync(user.Id, "claude", Stronger, Morning);

        // The only model that is measured as the Market Analyst was right once in 4 calls: 25
        // points below a half. The model that the role has is not measured, which counts as 0.
        await application.GiveCallsAsync(user.Id, agent, Role.MarketAnalyst, stronger, Morning.AddHours(-5), matched: 1, missed: 3);

        var form = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        // The strongest candidate is shown (FR-TEAM-4), and the role is not recommended it.
        Assert.Equal(
            "Strongest candidate: claude-b, of Claude. Strength score -12.5 percentage points, sample size 4, Demo data. " +
            "Not recommended: it does not score above what the role has, and the model that the role has has no strength score yet, which counts as 0.",
            AgentBrowser.CandidateOn(form, "market-analyst"));
        Assert.DoesNotContain("name=\"Apply\"", form);
    }

    [Fact]
    public async Task A_page_of_an_agent_in_Demo_names_Live_data_as_a_word_and_shows_no_Live_badge()
    {
        using var application = Application();
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning.AddDays(-2), name: "Swing");
        var stronger = await application.GiveModelAsync("claude", Stronger, Morning);
        await application.LetKeyUseAsync(user.Id, "claude", Stronger, Morning);

        // The model was measured in Live, where it has enough samples, and in Demo, where it
        // did better. Live is gone by, and the line says so (FR-EXP-9).
        await application.GiveCallsAsync(
            user.Id, agent, Role.MarketAnalyst, stronger, Morning.AddHours(-5), matched: 3, missed: 1, mode: TradingMode.Live);
        await application.GiveCallsAsync(
            user.Id, agent, Role.MarketAnalyst, stronger, Morning.AddHours(-5), matched: 12, missed: 0, mode: TradingMode.Demo);

        var form = await client.PageAsync(AgentBrowser.AgentPage(agent));

        Assert.StartsWith(
            "Strongest candidate: claude-b, of Claude. Strength score +12.5 percentage points, sample size 4, Live data.",
            AgentBrowser.CandidateOn(form, "market-analyst"));

        // The badge of a page is the mode of its agent, and no other (FR-UI-3).
        Assert.Contains(AgentBrowser.Badge(TradingMode.Demo), form);
        Assert.DoesNotContain(AgentBrowser.Badge(TradingMode.Live), form);
    }

    /// <summary>
    /// Gives a user an agent named Swing on Spot, on the hour, whose roles all have the model of
    /// the tests, and what its roles are measured by: as the Market Analyst, another model of
    /// Claude was right 4 times of 4 and the model of the tests 3 times of 4, and as the
    /// Strategist the other model made 1 R on each of 4 plans, which the model of the tests
    /// approved as the Risk Manager. The key of the user can use the other model, unless the
    /// test says otherwise.
    /// </summary>
    /// <returns>The id of the agent, and the id of the model that its roles have.</returns>
    private static async Task<(Guid Agent, Guid Team)> MeasuredAsync(
        AccountsApplication application,
        Guid userId,
        AgentState state = AgentState.Draft,
        TradingMode mode = TradingMode.Demo,
        bool keyUsesStronger = true)
    {
        var agent = await application.GiveAgentAsync(userId, Morning.AddDays(-2), name: "Swing", state: state, mode: mode);
        var team = await application.GiveModelAsync("claude", TeamModel, Morning);
        var stronger = await application.GiveModelAsync("claude", Stronger, Morning);

        if (keyUsesStronger)
        {
            await application.LetKeyUseAsync(userId, "claude", Stronger, Morning);
        }

        await application.GiveCallsAsync(userId, agent, Role.MarketAnalyst, stronger, Morning.AddHours(-5), matched: 4, missed: 0);
        await application.GiveCallsAsync(userId, agent, Role.MarketAnalyst, team, Morning.AddHours(-30), matched: 3, missed: 1);

        foreach (var hour in Enumerable.Range(0, 4))
        {
            await application.GiveTradeAsync(
                userId, agent, Morning.AddHours(-10 + hour), resultInR: 1m, roles: [(Role.Strategist, stronger), (Role.RiskManager, team)]);
        }

        return (agent, team);
    }

    /// <summary>The application, with a minimum sample and a damping that a test reaches with a few rows.</summary>
    private static AccountsApplication Application() =>
        AgentsApplication.With(
            new StandInMarketData(),
            new TestClock(Morning),
            settings: file => file.With("Experience:MinSamples", 4).With("Experience:Damping", 4));
}
