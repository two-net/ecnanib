using Ecnanib.Agents;
using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Catalog;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;
using Ecnanib.WebUi.Pages.Agents;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// When a model assigned to an agent becomes unavailable, the agent moves to Attention required,
/// and the system does not substitute another model on its own (FR-CAT-8). The Catalog tells
/// Agents inside the transaction that marks the model, and Agents moves the agents that run with
/// it, whoever they belong to. How the Catalog tells is in <c>ModelDependentsTests</c>.
/// </summary>
/// <remarks>
/// The file of the tests lets a model miss 3 syncs: the third sync in a row that lacks a model
/// marks it unavailable.
/// </remarks>
public class AgentModelDependentsTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    // Between two hours, so that the job that checks the stored keys is not due while the test runs.
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 9, 20, 0, TimeSpan.Zero);

    private static readonly ListedAiModel Opus = StandInModelLists.Model("claude-opus-5-5", "Claude Opus 5.5");
    private static readonly ListedAiModel Haiku = StandInModelLists.Model("claude-haiku-4-5", "Claude Haiku 4.5");

    [Fact]
    public async Task A_running_agent_requires_attention_when_a_model_of_its_team_becomes_unavailable_and_no_other_is_put_in_its_place()
    {
        // The service has a key of its own for Claude, which lists two models as the application starts.
        var clock = new TestClock(Start);
        using var application = AgentsApplication.With(new StandInMarketData(), clock, settings: file => file.WithCatalogKey());
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku);
        using var client = application.CreateClient();
        var user = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        await Wait.UntilAsync(() => StoredCatalogSync.All(application.Database).Count == 1, "the sync as the application first starts");
        await application.GiveKeysAsync(user.Id, Start);
        await application.GiveKeysAsync(grace.Id, Start);
        await client.SignInAsync(Ada);

        // Ada has an agent that runs with one role on the smaller model and three on the other,
        // one that runs with the other model alone, and one that she paused, with the smaller
        // model for every role. Grace has an agent that runs with the smaller model.
        var onHaiku = await application.GiveAgentAsync(user.Id, Start, name: "On Haiku", state: AgentState.Running, team: false);
        var opus = await application.GiveTeamAsync(user.Id, onHaiku, Start, model: "claude-opus-5-5");
        var haiku = await application.GiveRoleAsync(user.Id, onHaiku, Role.Strategist, Start, model: "claude-haiku-4-5");
        var onOpus = await application.GiveAgentAsync(
            user.Id, Start, name: "On Opus", state: AgentState.Running, symbol: "SOLUSDT", team: false);
        await application.GiveTeamAsync(user.Id, onOpus, Start, model: "claude-opus-5-5");
        var paused = await application.GiveAgentAsync(
            user.Id, Start, name: "Paused", state: AgentState.Paused, market: Market.Futures, team: false);
        await application.GiveTeamAsync(user.Id, paused, Start, model: "claude-haiku-4-5");
        var graces = await application.GiveAgentAsync(grace.Id, Start, name: "Grace's", state: AgentState.Running, team: false);
        await application.GiveTeamAsync(grace.Id, graces, Start, model: "claude-haiku-4-5");

        // The models are the ones of the catalog: the test gave the roles what a sync had found.
        Assert.Equal(
            [opus, haiku],
            new[] { "claude-opus-5-5", "claude-haiku-4-5" }.Select(model => StoredAiModel.Of(application.Database, "claude", model).Id));
        Assert.Equal(2, StoredAiModel.All(application.Database).Count);

        // Claude stops listing the smaller model. A model that is only missed is still active,
        // and an agent goes on with it.
        application.ModelLists.Answer = (_, _) => StandInModelLists.Listed(Opus);
        await SyncAsync(application, clock, minutes: 10);
        await SyncAsync(application, clock, minutes: 20);

        Assert.All(new[] { onHaiku, onOpus, graces }, agent => Assert.Equal("Running", AgentControlTests.StateOf(application, agent).State));

        // The third sync that lacks it marks it unavailable. The agents that ran with it require
        // attention, since then and for that reason, whoever they belong to (FR-CAT-8).
        await SyncAsync(application, clock, minutes: 30);

        Assert.Equal("Unavailable", StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5").Status);
        Assert.All(new[] { onHaiku, graces }, agent => Assert.Equal(
            ("AttentionRequired", "ModelUnavailable", Start.AddMinutes(30)), AgentControlTests.StateOf(application, agent)));
        Assert.Equal(["Running: Created", "AttentionRequired: ModelUnavailable"], StoredAgentStateChange.Told(application.Database, onHaiku));
        Assert.Equal(grace.Id, StoredAgentStateChange.Of(application.Database, graces)[^1].UserId);

        // The agent that uses the other model runs, and the one that did not run is as it was.
        Assert.Equal("Running", AgentControlTests.StateOf(application, onOpus).State);
        Assert.Equal("Paused", AgentControlTests.StateOf(application, paused).State);

        // No other model was put in the place of the one that is gone: every role has what its
        // user gave it.
        Assert.Equal(
            ["MarketAnalyst: claude-opus-5-5", "SentimentAnalyst: claude-opus-5-5", "Strategist: claude-haiku-4-5", "RiskManager: claude-opus-5-5"],
            StoredAgentRole.Told(application.Database, onHaiku));

        // The service did it, and no user: the audit trail has nothing about the agents.
        Assert.DoesNotContain(
            StoredAuditEvent.All(application.Database), recorded => recorded.Action.StartsWith("Agent", StringComparison.Ordinal));

        // The page of the agent says the state, and why. The form of its settings shows the
        // model that the role has, and that it cannot be selected now.
        var state = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentStatePage(onHaiku)));

        Assert.Contains("<strong>Attention required</strong>, since 15 November 2026, 09:50 UTC.", state);
        Assert.Contains($"<p>{AgentPages.Says(AgentStateCause.ModelUnavailable)}</p>", state);

        var editor = AgentBrowser.Read(await client.PageAsync(AgentBrowser.AgentPage(onHaiku)));

        Assert.Equal("Claude Haiku 4.5 (claude-haiku-4-5), cannot be selected now", AgentBrowser.ModelChosenOn(editor, "strategist"));
        Assert.Equal("Claude Opus 5.5 (claude-opus-5-5)", AgentBrowser.ModelChosenOn(editor, "risk-manager"));
        Assert.Contains($"<p role=\"note\">{AgentPages.ModelNotSelectableNow}</p>", editor);

        // The agent is not resumed with the model that is gone, and the page names the role.
        var (_, refused) = await client.ResumeAgentAsync(onHaiku);
        var read = AgentBrowser.Read(refused);

        Assert.Contains($"<p class=\"form-error\" role=\"alert\">{AgentPages.RolesBlock(AgentControl.Resume)}</p>", read);
        Assert.Contains(
            $"<li>{AgentPages.Says(new RoleBlock(Role.Strategist, RoleProblem.NotSelectable, "Claude", "Claude Haiku 4.5"))}</li>", read);
        Assert.Equal("AttentionRequired", AgentControlTests.StateOf(application, onHaiku).State);

        // She gives the role another model, and resumes the agent.
        clock.Set(Start.AddMinutes(35));
        var (saved, _) = await client.SaveAgentAsync(onHaiku, AgentBrowser.Entry(name: "On Haiku").WithRole("Strategist", "claude", opus));

        Assert.Equal(AgentBrowser.AgentsPage, saved);

        await client.ResumeAgentAsync(onHaiku);

        Assert.Equal(("Running", "UserResumed", Start.AddMinutes(35)), AgentControlTests.StateOf(application, onHaiku));
        Assert.Equal("Strategist: claude-opus-5-5", StoredAgentRole.Told(application.Database, onHaiku)[2]);
    }

    /// <summary>Syncs the catalog on demand, so many minutes after the application started.</summary>
    private static async Task SyncAsync(AccountsApplication application, TestClock clock, int minutes)
    {
        clock.Set(Start.AddMinutes(minutes));

        Assert.Single(await application.Catalog.SyncAsync(TestContext.Current.CancellationToken));
    }
}
