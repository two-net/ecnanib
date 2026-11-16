using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecnanib.Agents;
using Ecnanib.Credentials;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// Every agent has the four roles of its team (FR-TEAM-1). For each role its user selects the
/// provider and the specific model, and the choice is stored with the agent, so two agents can
/// use different models of the same provider (FR-AGT-5). By default the roles are filled by four
/// different providers, which the configuration file names (FR-TEAM-2, FR-TEAM-3). A user who
/// lacks a key for a provider gives its role another provider, so one provider may hold more
/// than one role, and the form warns that the team is reduced, and warns of its own when the
/// Strategist and the Risk Manager use the same provider (FR-TEAM-5). Only a model that can be
/// selected, and that the user's own key can use, is offered and stored (FR-CAT-6, FR-CAT-9).
/// </summary>
/// <remarks>
/// The file of the tests gives the roles to Claude, Grok, GPT, and Gemini, in the order of the
/// roles, and allows the models of Claude only. A test that needs the models of another provider
/// allows them.
/// </remarks>
public class AgentRoleTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    private const string NewAgentPage = AgentBrowser.NewAgentPage;

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_form_starts_from_the_provider_that_the_file_gives_each_role_and_offers_the_models_that_the_users_key_can_use()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), settings: AllowingEveryProvider);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // The catalog holds three models of Claude that can be selected and one that the file
        // denies, one of Grok, and one of GPT.
        await application.GiveModelAsync("claude", "claude-a", Morning, "Claude A");
        await application.GiveModelAsync("claude", "claude-b", Morning);
        await application.GiveModelAsync("claude", "claude-c", Morning);
        await application.GiveModelAsync("claude", "claude-next-preview", Morning);
        await application.GiveModelAsync("grok", "grok-live", Morning);
        await application.GiveModelAsync("gpt", "gpt-x", Morning);

        // Her key for Claude can use two of them and the one that is denied, and her key for
        // Grok its one. Gemini took her key and has listed nothing to it yet. She has no key
        // for GPT.
        await application.GiveAiKeyAsync(user.Id, "claude", Morning, ["claude-a", "claude-b", "claude-next-preview"]);
        await application.GiveAiKeyAsync(user.Id, "grok", Morning, ["grok-live"]);
        await application.GiveAiKeyAsync(user.Id, "gemini", Morning, accessibleModels: null);

        var form = await client.PageAsync(NewAgentPage);
        var read = AgentBrowser.Read(form);

        // Each role starts from the provider that the file gives it: four different ones
        // (FR-TEAM-2, FR-TEAM-3). The file of the tests names others than the tracked file.
        Assert.Equal(
            ["claude", "grok", "gpt", "gemini"],
            new[] { "market-analyst", "sentiment-analyst", "strategist", "risk-manager" }.Select(role => AgentBrowser.ProviderChosenOn(form, role)));
        Assert.Empty(AgentBrowser.WarningsOn(form));

        // The model of each is for her to choose, among those of the role's provider that her
        // own key can use, and that can be selected at all (FR-CAT-6, FR-CAT-9). A model says
        // the name its provider gives it, and that it can search live.
        Assert.All(
            new[] { "market-analyst", "sentiment-analyst", "strategist", "risk-manager" },
            role => Assert.Equal(AgentPages.ChooseModel, AgentBrowser.ModelChosenOn(form, role)));
        Assert.Equal(["Claude A (claude-a)", "claude-b"], AgentBrowser.ModelsOfferedOn(form, "market-analyst"));
        Assert.Equal(["grok-live, searches live"], AgentBrowser.ModelsOfferedOn(form, "sentiment-analyst"));
        Assert.Empty(AgentBrowser.ModelsOfferedOn(form, "strategist"));
        Assert.Empty(AgentBrowser.ModelsOfferedOn(form, "risk-manager"));

        // Where a provider offers her nothing, the form says why, and that another provider can
        // take the role (FR-TEAM-5).
        Assert.Contains(
            $"<p role=\"note\">{AgentPages.Says(Provider("gpt", "GPT", ModelBlock.NoKey))} <a href=\"{WebUiModule.KeysPath}\">Your keys</a></p>", read);
        Assert.Contains("You have no key for GPT. Store one on the page of your keys, or choose another provider for this role.", read);
        Assert.Contains(AgentPages.Says(Provider("gemini", "Gemini", ModelBlock.NotKnown))!, read);
        Assert.DoesNotContain("You have no key for Claude", read);

        // Every provider can be chosen for every role.
        Assert.Equal(4, Regex.Count(form, "<option value=\"gemini\"( selected)?>Gemini</option>"));
        Assert.Contains($"<button type=\"submit\" name=\"Show\" value=\"models\" formnovalidate>{AgentPages.ShowModelsButton}</button>", form);
    }

    [Fact]
    public async Task A_user_gives_each_role_a_model_and_the_choice_is_stored_with_the_agent()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock, settings: AllowingEveryProvider);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var claudeA = await application.GiveModelAsync("claude", "claude-a", Morning, "Claude A");
        var claudeB = await application.GiveModelAsync("claude", "claude-b", Morning);
        var grokLive = await application.GiveModelAsync("grok", "grok-live", Morning);
        await application.GiveAiKeyAsync(user.Id, "claude", Morning, ["claude-a", "claude-b"]);
        await application.GiveAiKeyAsync(user.Id, "grok", Morning, ["grok-live"]);

        // She gives three roles a model, and leaves the Risk Manager without one: a draft may
        // wait for a model. Two roles have models of the same provider.
        var (path, _) = await client.CreateAgentAsync(AgentBrowser.Entry(name: "First")
            .WithRole("MarketAnalyst", "claude", claudeA)
            .WithRole("SentimentAnalyst", "grok", grokLive)
            .WithRole("Strategist", "claude", claudeB)
            .WithRole("RiskManager", "gpt", model: null));

        Assert.Equal(AgentBrowser.AgentsPage, path);

        var first = StoredAgent.Named(application.Database, "First");

        Assert.Equal("Draft", first.State);
        Assert.Equal(
            ["MarketAnalyst: claude-a", "SentimentAnalyst: grok-live", "Strategist: claude-b"],
            StoredAgentRole.Told(application.Database, first.Id));
        Assert.All(StoredAgentRole.Of(application.Database, first.Id), role => Assert.Equal(user.Id, role.UserId));

        // Another agent of hers uses another model of the same provider: the choice is of the
        // agent, and not of the user or of the provider (FR-AGT-5).
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Second", symbol: "SOLUSDT").WithTeam("claude", claudeB));

        var second = StoredAgent.Named(application.Database, "Second");

        Assert.Equal(
            ["MarketAnalyst: claude-b", "SentimentAnalyst: claude-b", "Strategist: claude-b", "RiskManager: claude-b"],
            StoredAgentRole.Told(application.Database, second.Id));
        Assert.Equal(3, StoredAgentRole.Of(application.Database, first.Id).Count);

        // The form of the first agent shows what each role has. The role that has no model
        // starts from the provider that the file gives it.
        var editor = await client.PageAsync(AgentBrowser.AgentPage(first.Id));

        Assert.Equal(
            [("claude", "Claude A (claude-a)"), ("grok", "grok-live, searches live"), ("claude", "claude-b"), ("gemini", AgentPages.ChooseModel)],
            new[] { "market-analyst", "sentiment-analyst", "strategist", "risk-manager" }
                .Select(role => (AgentBrowser.ProviderChosenOn(editor, role), AgentBrowser.ModelChosenOn(editor, role))));

        // She gives the Strategist another model, and the Risk Manager its first. The form
        // brings the other roles as they are, and they stay as they are.
        clock.Set(Morning.AddMinutes(10));
        var (saved, _) = await client.SaveAgentAsync(first.Id, AgentBrowser.Entry(name: "First")
            .WithRole("MarketAnalyst", "claude", claudeA)
            .WithRole("SentimentAnalyst", "grok", grokLive)
            .WithRole("Strategist", "claude", claudeA)
            .WithRole("RiskManager", "grok", grokLive));

        Assert.Equal(AgentBrowser.AgentsPage, saved);
        Assert.Equal(
            ["MarketAnalyst: claude-a", "SentimentAnalyst: grok-live", "Strategist: claude-a", "RiskManager: grok-live"],
            StoredAgentRole.Told(application.Database, first.Id));

        // Nothing of the other agent changed, and nothing else of this one: giving a role a
        // model changes no state, and is no event of the audit trail (NFR-LOG-7).
        Assert.Equal(4, StoredAgentRole.Of(application.Database, second.Id).Count(role => role.ModelId == claudeB));
        Assert.Equal(first, StoredAgent.Named(application.Database, "First"));
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));

        // The export of her data names the model of each role, by its provider and its
        // identifier (NFR-SEC-9).
        var export = JsonDocument.Parse(await client.PageAsync(WebUiModule.ExportPath)).RootElement;

        Assert.Equal(
            [
                (first.Id, "MarketAnalyst", "claude", "claude-a"),
                (first.Id, "SentimentAnalyst", "grok", "grok-live"),
                (first.Id, "Strategist", "claude", "claude-a"),
                (first.Id, "RiskManager", "grok", "grok-live"),
            ],
            export.GetProperty("agentRoles").EnumerateArray()
                .Where(role => role.GetProperty("agentId").GetGuid() == first.Id)
                .Select(role => (
                    role.GetProperty("agentId").GetGuid(),
                    role.GetProperty("role").GetString()!,
                    role.GetProperty("provider").GetString()!,
                    role.GetProperty("model").GetString()!)));
        Assert.Equal(8, export.GetProperty("agentRoles").GetArrayLength());
    }

    [Fact]
    [ProvesOwnershipOf("agent_roles")]
    public async Task Only_a_model_that_the_users_own_key_can_use_is_stored_whatever_a_form_brings()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), settings: AllowingEveryProvider);

        var claudeA = await application.GiveModelAsync("claude", "claude-a", Morning);
        var claudeB = await application.GiveModelAsync("claude", "claude-b", Morning);
        var preview = await application.GiveModelAsync("claude", "claude-next-preview", Morning);
        var gptX = await application.GiveModelAsync("gpt", "gpt-x", Morning);

        // Grace's key for Claude can use one model, and Ada's another. Ada's can also use a
        // model that the file denies. Grace has an agent with a team.
        var grace = await application.RegisterVerifiedAsync(Grace);
        await application.GiveAiKeyAsync(grace.Id, "claude", Morning, ["claude-b"]);
        var graces = await application.GiveAgentAsync(grace.Id, Morning, name: "Grace's", team: false);
        await application.GiveTeamAsync(grace.Id, graces, Morning, model: "claude-b");
        var gracesRows = application.RowsOf(grace.Id);

        var ada = await application.RegisterVerifiedAsync(Ada);
        await application.GiveAiKeyAsync(ada.Id, "claude", Morning, ["claude-a", "claude-next-preview"]);
        using var client = application.CreateClient();
        await client.SignInAsync(Ada);

        // Ada is offered the model that her own key can use, and no other.
        var form = await client.PageAsync(NewAgentPage);

        Assert.Equal(["claude-a"], AgentBrowser.ModelsOfferedOn(form, "market-analyst"));
        Assert.DoesNotContain(claudeB.ToString(), form);

        // A form that brings other models all the same is refused, role by role, with
        // everything else that is wrong with it: a model that only another user's key can use,
        // one that the file denies, one of a provider that she has no key for, and something
        // that is no model. Nothing is stored (FR-CAT-6, FR-CAT-9).
        var (path, refused) = await client.CreateAgentAsync(AgentBrowser.Entry(name: "")
            .WithRole("MarketAnalyst", "claude", claudeB)
            .WithRole("SentimentAnalyst", "claude", preview)
            .WithRole("Strategist", "gpt", gptX)
            .WithRole("RiskManager", "claude", model: null)
            .With("RiskManagerModel", "claude-a"));

        Assert.Equal(NewAgentPage, path);
        Assert.Equal(
            [
                "Enter a name.",
                AgentPages.Says(Role.MarketAnalyst, RoleProblem.NotListed, "Claude"),
                AgentPages.Says(Role.SentimentAnalyst, RoleProblem.NotSelectable, "Claude"),
                AgentPages.Says(Role.Strategist, RoleProblem.NoKey, "GPT"),
                "Choose a model for the Risk Manager from the list.",
            ],
            ErrorsOn(refused));
        Assert.Equal(5, Regex.Count(refused, "aria-invalid=\"true\""));
        Assert.Empty(StoredAgent.Of(application.Database, ada.Id));
        Assert.DoesNotContain(StoredAgentRole.All(application.Database), role => role.UserId == ada.Id);

        // Grace's agent is out of her reach: there is no such page for her, and a form posted
        // for it changes no role of it. The page that says "not found" has no form that saves,
        // so that answer is the one of a form which no page has.
        var token = Browser.AntiforgeryTokenIn(form);

        using var page = await client.GetAsync(AgentBrowser.AgentPage(graces), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);

        using var answer = await client.SendFormAsync(
            AgentBrowser.AgentPage(graces), AgentBrowser.SaveForm, token, AgentBrowser.Entry(name: "Taken").WithTeam("claude", claudeA));

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(gracesRows["agent_roles"], application.RowsOf(grace.Id)["agent_roles"]);
        Assert.Equal(gracesRows["agents"], application.RowsOf(grace.Id)["agents"]);

        // What Ada stores is hers: an agent with the model that her key can use.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Ada's").WithTeam("claude", claudeA));

        var adas = StoredAgent.Named(application.Database, "Ada's");

        Assert.Equal(4, StoredAgentRole.Of(application.Database, adas.Id).Count(role => role.UserId == ada.Id && role.ModelId == claudeA));
        Assert.Equal(gracesRows["agent_roles"], application.RowsOf(grace.Id)["agent_roles"]);
    }

    [Fact]
    public async Task A_provider_may_hold_more_than_one_role_and_the_form_warns_that_the_team_is_reduced()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), settings: AllowingEveryProvider);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // She has a key for Claude, and for no other provider.
        var claudeA = await application.GiveModelAsync("claude", "claude-a", Morning);
        await application.GiveAiKeyAsync(user.Id, "claude", Morning, ["claude-a"]);

        // The form says of each of the other roles that she has no key for its provider, and
        // that another provider can take the role (FR-TEAM-5). It warns of nothing yet.
        var form = AgentBrowser.Read(await client.PageAsync(NewAgentPage));

        Assert.All(
            new[] { "Grok", "GPT", "Gemini" },
            name => Assert.Contains($"You have no key for {name}. Store one on the page of your keys, or choose another provider for this role.", form));
        Assert.Empty(AgentBrowser.WarningsOn(form));

        // She gives the Sentiment Analyst to Claude as well, and asks for its models. Nothing
        // is stored. The list is that of Claude now, and the form warns that the team is reduced.
        var (path, shown) = await client.CreateAgentAsync(
        [
            .. AgentBrowser.Entry(name: "Reduced")
                .WithRole("MarketAnalyst", "claude", model: null)
                .WithRole("SentimentAnalyst", "claude", model: null)
                .WithRole("Strategist", "gpt", model: null)
                .WithRole("RiskManager", "gemini", model: null),
            ("Show", "models"),
        ]);

        Assert.Equal(NewAgentPage, path);
        Assert.Empty(StoredAgent.All(application.Database));
        Assert.Equal("claude", AgentBrowser.ProviderChosenOn(shown, "sentiment-analyst"));
        Assert.Equal(["claude-a"], AgentBrowser.ModelsOfferedOn(shown, "sentiment-analyst"));
        Assert.Contains("value=\"Reduced\"", shown);
        Assert.Equal([AgentPages.ReducedTeam(["Claude"])], AgentBrowser.WarningsOn(shown));
        Assert.Equal(
            "The team is reduced: Claude holds more than one role. A team works best with four different providers, which do " +
            "not share the same blind spots. You can save it as it is.",
            AgentPages.ReducedTeam(["Claude"]));

        // With the Strategist and the Risk Manager on one provider, the form warns of that of
        // its own: the one reviews the plan of the other.
        var (_, both) = await client.CreateAgentAsync(
        [
            .. AgentBrowser.Entry(name: "Reduced")
                .WithRole("MarketAnalyst", "claude", model: null)
                .WithRole("SentimentAnalyst", "grok", model: null)
                .WithRole("Strategist", "gpt", model: null)
                .WithRole("RiskManager", "gpt", model: null),
            ("Show", "models"),
        ]);

        Assert.Equal([AgentPages.ReducedTeam(["GPT"]), AgentPages.SameProviderReviews("GPT")], AgentBrowser.WarningsOn(both));
        Assert.Contains("The Strategist and the Risk Manager both use GPT.", AgentPages.SameProviderReviews("GPT"));

        // Nothing keeps her from a team of one provider: it is stored as she chose it.
        var (saved, _) = await client.CreateAgentAsync(AgentBrowser.Entry(name: "Reduced").WithTeam("claude", claudeA));

        Assert.Equal(AgentBrowser.AgentsPage, saved);

        var agent = StoredAgent.Named(application.Database, "Reduced");

        Assert.Equal(4, StoredAgentRole.Of(application.Database, agent.Id).Count);

        // And the form of the agent warns of both whenever it is shown.
        Assert.Equal(
            [AgentPages.ReducedTeam(["Claude"]), AgentPages.SameProviderReviews("Claude")],
            AgentBrowser.WarningsOn(await client.PageAsync(AgentBrowser.AgentPage(agent.Id))));
    }

    [Fact]
    public async Task A_model_of_another_provider_than_the_one_chosen_is_refused_and_a_role_that_has_a_model_keeps_one()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning), settings: AllowingEveryProvider);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        var grokLive = await application.GiveModelAsync("grok", "grok-live", Morning);
        await application.GiveAiKeyAsync(user.Id, "grok", Morning, ["grok-live"]);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", team: false);
        var claudeA = await application.GiveTeamAsync(user.Id, agent, Morning, model: "claude-a");
        var team = StoredAgentRole.Told(application.Database, agent);

        // She chooses Grok for the Strategist, and saves without choosing one of its models:
        // the form still brings the model that the role has. Nothing is saved, the list is that
        // of Grok now, and the form says what to do.
        var (path, unfinished) = await client.SaveAgentAsync(
            agent, AgentBrowser.Entry(name: "Swing").WithTeam("claude", claudeA).WithRole("Strategist", "grok", claudeA));

        Assert.Equal(AgentBrowser.AgentPage(agent), path);
        Assert.Equal([AgentPages.Says(Role.Strategist, RoleProblem.NotOfProvider, "Grok")], ErrorsOn(unfinished));
        Assert.Equal(["grok-live, searches live"], AgentBrowser.ModelsOfferedOn(unfinished, "strategist"));
        Assert.Equal(AgentPages.ChooseModel, AgentBrowser.ModelChosenOn(unfinished, "strategist"));
        Assert.Equal(team, StoredAgentRole.Told(application.Database, agent));

        // A model of Grok beside Claude is refused in the same way.
        var (_, mismatched) = await client.SaveAgentAsync(
            agent, AgentBrowser.Entry(name: "Swing").WithTeam("claude", claudeA).WithRole("Strategist", "claude", grokLive));

        Assert.Equal([AgentPages.Says(Role.Strategist, RoleProblem.NotOfProvider, "Claude")], ErrorsOn(mismatched));

        // A role that has a model keeps one: a form that says it has none is refused.
        var (_, cleared) = await client.SaveAgentAsync(
            agent, AgentBrowser.Entry(name: "Swing").WithTeam("claude", claudeA).WithRole("Strategist", "claude", model: null));

        Assert.Equal(["Choose a model for the Strategist. A role that has a model keeps one: you can give it another."], ErrorsOn(cleared));
        Assert.Equal(team, StoredAgentRole.Told(application.Database, agent));

        // A form that brings nothing of the roles says nothing about them, and they stay.
        var (renamed, _) = await client.SaveAgentAsync(agent, AgentBrowser.Entry(name: "Renamed"));

        Assert.Equal(AgentBrowser.AgentsPage, renamed);
        Assert.Equal(team, StoredAgentRole.Told(application.Database, agent));

        // With a model of Grok chosen beside Grok, the Strategist has it.
        var (saved, _) = await client.SaveAgentAsync(
            agent, AgentBrowser.Entry(name: "Renamed").WithTeam("claude", claudeA).WithRole("Strategist", "grok", grokLive));

        Assert.Equal(AgentBrowser.AgentsPage, saved);
        Assert.Equal(
            ["MarketAnalyst: claude-a", "SentimentAnalyst: claude-a", "Strategist: grok-live", "RiskManager: claude-a"],
            StoredAgentRole.Told(application.Database, agent));
    }

    [Fact]
    public async Task The_settings_of_an_agent_are_saved_while_a_key_of_its_team_is_gone_and_the_form_shows_the_model_that_it_has()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing");
        var model = Assert.Single(StoredAgentRole.Of(application.Database, agent).Select(role => role.ModelId).Distinct());

        // She deletes her key for Claude, which every role of the agent uses.
        await client.DeleteAiKeyAsync("claude");

        // The form shows the model that each role has, says that it cannot be selected now, and
        // offers no other of the provider.
        var editor = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(agent)));

        Assert.Equal("claude-test-model, cannot be selected now", AgentBrowser.ModelChosenOn(editor, "strategist"));
        Assert.Equal(["claude-test-model, cannot be selected now"], AgentBrowser.ModelsOfferedOn(editor, "strategist"));
        Assert.Equal(4, Regex.Count(editor, Regex.Escape($"<p role=\"note\">{AgentPages.ModelNotSelectableNow}</p>")));
        Assert.Contains("You have no key for Claude.", editor);

        // Her other settings are saved all the same, with the form as it is: a role that keeps
        // its model is not asked about again.
        var (path, _) = await client.SaveAgentAsync(agent, AgentBrowser.Entry(name: "Renamed", capital: "2000").WithTeam("claude", model));

        Assert.Equal(AgentBrowser.AgentsPage, path);
        Assert.Equal(("Renamed", 2000m), (StoredAgent.Named(application.Database, "Renamed").Name, StoredAgent.Named(application.Database, "Renamed").AllocatedCapital));
        Assert.Equal(4, StoredAgentRole.Of(application.Database, agent).Count(role => role.ModelId == model));

        // A role is not given a model of that provider anew, though: another agent of hers is
        // refused the same model.
        var (_, refused) = await client.CreateAgentAsync(AgentBrowser.Entry(name: "Another", symbol: "SOLUSDT").WithRole("Strategist", "claude", model));

        Assert.Equal([AgentPages.Says(Role.Strategist, RoleProblem.NoKey, "Claude")], ErrorsOn(refused));
        Assert.Single(StoredAgent.All(application.Database));
    }

    [Fact]
    public async Task Changing_the_model_of_a_role_of_an_agent_in_Live_asks_once_more()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", state: AgentState.Running, mode: TradingMode.Live);
        var has = Assert.Single(StoredAgentRole.Of(application.Database, agent).Select(role => role.ModelId).Distinct());
        var other = await application.GiveModelAsync("claude", "claude-b", Morning);
        await application.LetKeyUseAsync(user.Id, "claude", "claude-b", Morning);
        var team = StoredAgentRole.Told(application.Database, agent);

        // A change to the model of a role is a change to what the agent does with real money:
        // nothing is stored, and the form asks once more, as it is (FR-UI-3).
        var change = AgentBrowser.Entry(name: "Swing").WithTeam("claude", has).WithRole("RiskManager", "claude", other);
        var (path, asks) = await client.SaveAgentAsync(agent, change);

        Assert.Equal(AgentBrowser.AgentPage(agent), path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.ConfirmSaveForLive}</p>", AgentBrowser.Read(asks));
        Assert.Contains("the models of the roles", AgentPages.ConfirmSaveForLive);
        Assert.Equal("claude-b", AgentBrowser.ModelChosenOn(asks, "risk-manager"));
        Assert.Equal(team, StoredAgentRole.Told(application.Database, agent));

        // The button that says so once more stores it. The agent runs on: the change applies
        // from its next cycle (FR-AGT-8).
        var (saved, _) = await client.SaveAgentAsync(agent, [.. change, ("Confirm", "live")]);

        Assert.Equal(AgentBrowser.AgentsPage, saved);
        Assert.Equal("RiskManager: claude-b", StoredAgentRole.Told(application.Database, agent)[^1]);
        Assert.Equal("Running", StoredAgent.Named(application.Database, "Swing").State);

        // A form that brings the team as it is asks nothing of an agent in Live.
        var (unchanged, _) = await client.SaveAgentAsync(
            agent, AgentBrowser.Entry(name: "Swing again").WithTeam("claude", has).WithRole("RiskManager", "claude", other));

        Assert.Equal(AgentBrowser.AgentsPage, unchanged);
    }

    [Fact]
    public async Task A_copy_of_an_agent_has_the_models_of_its_roles()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        // An agent with three roles on one model, and one on another.
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing");
        await application.GiveRoleAsync(user.Id, agent, Role.Strategist, Morning, model: "claude-b");
        var bare = await application.GiveAgentAsync(user.Id, Morning, name: "Bare", symbol: "SOLUSDT", team: false);

        await client.CloneAgentAsync(agent);
        await client.CloneAgentAsync(bare);

        // The copy has the same model for each role, in rows of its own. The agent keeps its own.
        var copy = StoredAgent.Named(application.Database, "Copy of Swing");

        Assert.Equal(
            ["MarketAnalyst: claude-test-model", "SentimentAnalyst: claude-test-model", "Strategist: claude-b", "RiskManager: claude-test-model"],
            StoredAgentRole.Told(application.Database, copy.Id));
        Assert.Equal(StoredAgentRole.Told(application.Database, agent), StoredAgentRole.Told(application.Database, copy.Id));
        Assert.All(StoredAgentRole.Of(application.Database, copy.Id), role => Assert.Equal(user.Id, role.UserId));

        // A copy of an agent whose roles have no model has none either.
        Assert.Empty(StoredAgentRole.Of(application.Database, StoredAgent.Named(application.Database, "Copy of Bare").Id));

        // Deleting the copy deletes its roles, and none of the agent's.
        await client.DeleteAgentAsync(copy.Id);

        Assert.Empty(StoredAgentRole.Of(application.Database, copy.Id));
        Assert.Equal(4, StoredAgentRole.Of(application.Database, agent).Count);
    }

    [Fact]
    public async Task An_agent_is_started_only_once_every_role_has_a_model_that_its_users_keys_can_use()
    {
        var clock = new TestClock(Morning);
        using var application = AgentsApplication.With(new StandInMarketData(), clock);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await application.GiveKeysAsync(user.Id, Morning);
        await client.SignInAsync(Ada);

        // A draft is created without a team: its user has a key for Claude that no model was
        // listed to yet, so no model is offered.
        await client.CreateAgentAsync(AgentBrowser.Entry(name: "Swing"));
        var agent = Assert.Single(StoredAgent.All(application.Database)).Id;

        Assert.Empty(StoredAgentRole.Of(application.Database, agent));

        // It is not started: the page names every role, and leads to the settings.
        var (path, refused) = await client.StartAgentAsync(agent);
        var read = AgentBrowser.Read(refused);

        Assert.Equal(AgentBrowser.AgentStatePage(agent), path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.RolesBlock(AgentControl.Start)}</p>", read);
        Assert.All(
            new[] { "Market Analyst", "Sentiment Analyst", "Strategist", "Risk Manager" },
            role => Assert.Contains($"<li>The {role} has no model. Choose one on the page of the agent's settings.</li>", read));
        Assert.Contains($"<a href=\"{AgentBrowser.AgentPage(agent)}\">Settings of the agent</a>", refused);
        Assert.DoesNotContain("Your keys", refused);
        Assert.Equal("Draft", AgentControlTests.StateOf(application, agent).State);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentStarted");

        // With three roles given a model, it waits for the fourth.
        foreach (var role in new[] { Role.MarketAnalyst, Role.SentimentAnalyst, Role.Strategist })
        {
            await application.GiveRoleAsync(user.Id, agent, role, Morning);
        }

        var waits = AgentBrowser.Read((await client.StartAgentAsync(agent)).Page);

        Assert.Equal(1, Regex.Count(waits, "<li>The [A-Za-z ]+ has no model\\."));
        Assert.Contains("<li>The Risk Manager has no model. Choose one on the page of the agent's settings.</li>", waits);
        Assert.Equal("Draft", AgentControlTests.StateOf(application, agent).State);

        // With a model that her key cannot use it does not start either: the page says which.
        var modelId = await application.GiveModelAsync("claude", "claude-b", Morning, "Claude B");
        await using (var context = await application.Contexts.CreateDbContextAsync(TestContext.Current.CancellationToken))
        {
            context.Add(new AgentRole(agent, user.Id, Role.RiskManager, modelId));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var cannotUse = AgentBrowser.Read((await client.StartAgentAsync(agent)).Page);

        Assert.Contains(
            $"<li>{AgentPages.Says(new RoleBlock(Role.RiskManager, RoleProblem.NotListed, "Claude", "Claude B"))}</li>", cannotUse);
        Assert.Contains($"<a href=\"{WebUiModule.KeysPath}\">Your keys</a>", cannotUse);
        Assert.Equal("Draft", AgentControlTests.StateOf(application, agent).State);

        // With the whole team it starts.
        await application.LetKeyUseAsync(user.Id, "claude", "claude-b", Morning);
        clock.Set(Morning.AddMinutes(5));

        Assert.Equal(AgentBrowser.AgentStatePage(agent), (await client.StartAgentAsync(agent)).Path);
        Assert.Equal(("Running", "UserStarted", Morning.AddMinutes(5)), AgentControlTests.StateOf(application, agent));
    }

    [Fact]
    public async Task An_agent_in_Live_is_not_started_without_its_team_by_the_page_that_asks_once_more()
    {
        using var application = AgentsApplication.With(new StandInMarketData(), new TestClock(Morning));
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);
        await client.StoreLiveKeyAsync();

        // An agent in Live with a valid Live key, and two roles that have no model.
        var agent = await application.GiveAgentAsync(user.Id, Morning, name: "Swing", mode: TradingMode.Live, team: false);
        await application.GiveRoleAsync(user.Id, agent, Role.MarketAnalyst, Morning);
        await application.GiveRoleAsync(user.Id, agent, Role.SentimentAnalyst, Morning);

        // The page that asks once more answers as the page of the state does: nothing is
        // started, and it names the roles that wait for a model.
        var (path, refused) = await client.ConfirmControlAsync(agent, "start");
        var read = AgentBrowser.Read(refused);

        Assert.Equal(AgentBrowser.ConfirmControlPage(agent, "start"), path);
        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.RolesBlock(AgentControl.Start)}</p>", read);
        Assert.Equal(
            ["The Strategist has no model. Choose one on the page of the agent's settings.", "The Risk Manager has no model. Choose one on the page of the agent's settings."],
            Regex.Matches(read, "<li>([^<]*)</li>").Select(role => role.Groups[1].Value));
        Assert.Equal("Draft", AgentControlTests.StateOf(application, agent).State);
        Assert.DoesNotContain(StoredAuditEvent.All(application.Database), recorded => recorded.Action == "AgentStarted");

        // With its whole team it is started there.
        await application.GiveTeamAsync(user.Id, agent, Morning);

        Assert.Equal(AgentBrowser.AgentStatePage(agent), (await client.ConfirmControlAsync(agent, "start")).Path);
        Assert.Equal("Running", AgentControlTests.StateOf(application, agent).State);
    }

    [Fact]
    public void The_team_is_reduced_when_a_provider_holds_more_than_one_role()
    {
        var claude = new AiProviderCode("claude");
        var gemini = new AiProviderCode("gemini");
        var gpt = new AiProviderCode("gpt");
        var grok = new AiProviderCode("grok");

        // Four different providers: nothing to warn of.
        var whole = AgentRoleRules.WarningsOf(Team(gpt, grok, gemini, claude));

        Assert.False(whole.Reduced);
        Assert.Empty(whole.Repeated);
        Assert.Null(whole.StrategistAndRiskManager);

        // One provider on two roles that are not the Strategist and the Risk Manager together.
        var reduced = AgentRoleRules.WarningsOf(Team(claude, claude, gemini, gpt));

        Assert.True(reduced.Reduced);
        Assert.Equal([claude], reduced.Repeated);
        Assert.Null(reduced.StrategistAndRiskManager);

        // The Strategist and the Risk Manager on one provider, which is said of its own.
        var reviewsItself = AgentRoleRules.WarningsOf(Team(gpt, grok, claude, claude));

        Assert.Equal([claude], reviewsItself.Repeated);
        Assert.Equal(claude, reviewsItself.StrategistAndRiskManager);

        // Two providers that each hold two roles, in the order of their codes.
        Assert.Equal([claude, grok], AgentRoleRules.WarningsOf(Team(grok, claude, grok, claude)).Repeated);

        // Roles that have no provider yet are not counted.
        Assert.False(AgentRoleRules.WarningsOf(new Dictionary<Role, AiProviderCode> { [Role.Strategist] = claude }).Reduced);
    }

    /// <summary>The providers of a team, in the order of the roles.</summary>
    private static Dictionary<Role, AiProviderCode> Team(
        AiProviderCode marketAnalyst, AiProviderCode sentimentAnalyst, AiProviderCode strategist, AiProviderCode riskManager) =>
        new()
        {
            [Role.MarketAnalyst] = marketAnalyst,
            [Role.SentimentAnalyst] = sentimentAnalyst,
            [Role.Strategist] = strategist,
            [Role.RiskManager] = riskManager,
        };

    /// <summary>The file of the tests, with the models of every provider allowed by their names.</summary>
    private static SettingsJson AllowingEveryProvider(SettingsJson file) => file
        .WithJson("AiProviders:Gemini:Allow", "[\"gemini-*\"]")
        .WithJson("AiProviders:GPT:Allow", "[\"gpt-*\"]")
        .WithJson("AiProviders:Grok:Allow", "[\"grok-*\"]");

    /// <summary>A provider that offers a user nothing, with why.</summary>
    private static ProviderChoice Provider(string code, string name, ModelBlock block) =>
        new(new AiProviderCode(code), name, block, []);

    /// <summary>What a page says is wrong, at the fields, in the order of the page.</summary>
    private static IReadOnlyList<string> ErrorsOn(string page) =>
        [.. Regex.Matches(page, "<p class=\"field-error\" role=\"alert\">([^<]*)</p>").Select(error => WebUtility.HtmlDecode(error.Groups[1].Value))];
}
