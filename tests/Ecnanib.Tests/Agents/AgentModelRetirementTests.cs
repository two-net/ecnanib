using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Credentials;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Notifications;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecnanib.Tests.Agents;

/// <summary>
/// FR-CAT-04, AC-09 and UC-07 for agents: a deprecated model warns every agent that isn't archived and uses it,
/// once, and a retired one moves each role it fills to the role's fallback, or pauses an Active agent whose
/// required role has none. The owner is notified both times.
/// </summary>
public sealed class AgentModelRetirementTests(PostgresFixture postgres)
{
    private const string Retiring = "model-x";
    private const string Fallback = "model-y";
    private const string CheckJob = "agent-model-check";
    private const string RetireAfter = "Catalog:RetireAfterMissingDays";

    private static CancellationToken Cancellation => AgentApp.Cancellation;

    /// <summary>UC-07, with a fallback that can take over.</summary>
    [Fact]
    public async Task A_deprecated_model_warns_once_and_a_retired_one_hands_its_role_to_the_fallback()
    {
        await using var app = new AgentApp(postgres);
        app.Lists(CredentialApp.AiKey, AgentApp.Model, Retiring);
        app.Lists(CredentialApp.OtherAiKey, Fallback);
        var user = await app.UserAsync();
        await app.AddAiAsync(user.Id, AgentApp.Second, CredentialApp.OtherAiKey);
        var agent = await ActiveAsync(
            app, user, new RoleAssignment(AiRole.Strategist, new ModelRef(AgentApp.Provider, Retiring), new ModelRef(AgentApp.Second, Fallback)));

        app.Lists(CredentialApp.AiKey, AgentApp.Model);
        await SyncAsync(app);

        Assert.Equal([$"ModelDeprecated /agents/{agent.Id}/settings"], await NotificationsAsync(app, user.Id));
        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));

        app.Clock.Advance(TimeSpan.FromHours(12));
        await SyncAsync(app, changes: false);
        await app.RunJobAsync("ModelLifecycleJob");

        Assert.Single(await NotificationsAsync(app, user.Id));

        app.Clock.Advance(TimeSpan.FromDays(7));
        await SyncAsync(app);

        var after = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal(AgentStatus.Active, after.Status);
        Assert.Equal(new RoleAssignment(AiRole.Strategist, new ModelRef(AgentApp.Second, Fallback)), after.Settings.RoleOf(AiRole.Strategist));
        Assert.True(after.Version > agent.Version);
        Assert.Equal(
            [$"ModelDeprecated /agents/{agent.Id}/settings", $"ModelRetired /agents/{agent.Id}/settings"],
            await NotificationsAsync(app, user.Id));

        var switched = (await app.AuditAsync(user.Id))[^1];
        Assert.Equal(("AgentUpdated", "System"), (switched.Action, switched.ActorKind));
        Assert.Contains("Role.Strategist", switched.Summary, StringComparison.Ordinal);
        Assert.Contains($"{AgentApp.Second} {Fallback}", switched.Summary, StringComparison.Ordinal);
        Assert.Contains("Fallback.Strategist", switched.Summary, StringComparison.Ordinal);
    }

    /// <summary>UC-07 without a fallback: only an Active agent can pause, and an archived one isn't told.</summary>
    [Fact]
    public async Task A_retired_model_with_no_fallback_pauses_the_active_agent_and_tells_every_agent_that_is_not_archived()
    {
        await using var app = new AgentApp(postgres, new() { [RetireAfter] = "1" });
        app.Lists(CredentialApp.AiKey, AgentApp.Model, Retiring);
        var user = await app.UserAsync();
        var other = await app.UserAsync();
        var strategist = new RoleAssignment(AiRole.Strategist, new ModelRef(AgentApp.Provider, Retiring));
        var active = await ActiveAsync(app, user, strategist);
        var draft = await app.CreateAsync(user.Id, With(user, "Draft", strategist));
        var archived = await app.CreateAsync(user.Id, With(user, "Old", strategist));
        Assert.Equal(TransitionOutcome.Done, (await app.Agents.ArchiveAsync(user.Id, archived.Id, Cancellation)).Outcome);
        var unaffected = await app.ActiveAsync(other);

        app.Lists(CredentialApp.AiKey, AgentApp.Model);
        await SyncAsync(app);
        app.Clock.Advance(TimeSpan.FromDays(1));
        await SyncAsync(app);

        var paused = (await app.Agents.FindAsync(user.Id, active.Id, Cancellation))!;
        Assert.Equal((AgentStatus.Paused, AgentStatusReason.ModelRetired), (paused.Status, paused.StatusReason));
        Assert.Equal(strategist, paused.Settings.RoleOf(AiRole.Strategist));
        Assert.Equal(AgentStatus.Draft, await app.StatusAsync(user.Id, draft.Id));
        Assert.Equal(AgentStatus.Archived, await app.StatusAsync(user.Id, archived.Id));
        Assert.Equal(AgentStatus.Active, await app.StatusAsync(other.Id, unaffected.Id));
        Assert.Equal(
            [
                $"ModelDeprecated /agents/{active.Id}/settings",
                $"ModelDeprecated /agents/{draft.Id}/settings",
                $"ModelRetired /agents/{active.Id}/settings",
                $"ModelRetired /agents/{draft.Id}/settings",
            ],
            await NotificationsAsync(app, user.Id));
        Assert.Empty(await NotificationsAsync(app, other.Id));

        var pause = (await app.AuditAsync(user.Id)).Single(row => row.Action == "AgentPaused");
        Assert.Equal(("System", active.Id.ToString()), (pause.ActorKind, pause.TargetId));
        Assert.Contains("ModelRetired", pause.Summary, StringComparison.Ordinal);
    }

    /// <summary>An optional role is skipped when its model fails (FR-ROLE-06), and a fallback only stands by, so neither pauses.</summary>
    [Fact]
    public async Task A_retired_optional_role_or_fallback_only_tells_the_owner()
    {
        await using var app = new AgentApp(postgres, new() { [RetireAfter] = "1" });
        app.Lists(CredentialApp.AiKey, AgentApp.Model, Retiring);
        var user = await app.UserAsync();
        var sentiment = new RoleAssignment(AiRole.SentimentAnalyst, new ModelRef(AgentApp.Provider, Retiring));
        var strategist = new RoleAssignment(AiRole.Strategist, new ModelRef(AgentApp.Provider, AgentApp.Model), new ModelRef(AgentApp.Provider, Retiring));
        var agent = await ActiveAsync(app, user, sentiment, strategist);

        app.Lists(CredentialApp.AiKey, AgentApp.Model);
        await SyncAsync(app);
        app.Clock.Advance(TimeSpan.FromDays(1));
        await SyncAsync(app);

        var after = (await app.Agents.FindAsync(user.Id, agent.Id, Cancellation))!;
        Assert.Equal(AgentStatus.Active, after.Status);
        Assert.Equal((sentiment, strategist), (after.Settings.RoleOf(AiRole.SentimentAnalyst), after.Settings.RoleOf(AiRole.Strategist)));
        Assert.Equal(after.Version, agent.Version);
        Assert.Equal(["ModelDeprecated", "ModelRetired"], (await NotificationsAsync(app, user.Id)).Select(row => row.Split(' ')[0]));
    }

    /// <summary>An in-process event is lost with the process, and the hourly check is the recovery path.</summary>
    [Fact]
    public async Task The_hourly_check_acts_on_a_retirement_nobody_heard_of()
    {
        await using var app = new AgentApp(
            postgres, new() { [RetireAfter] = "1" }, services: collection => collection.RemoveAll<IModelStatusObserver>());
        app.Lists(CredentialApp.AiKey, AgentApp.Model, Retiring);
        var user = await app.UserAsync();
        var agent = await ActiveAsync(app, user, new RoleAssignment(AiRole.Strategist, new ModelRef(AgentApp.Provider, Retiring)));

        app.Lists(CredentialApp.AiKey, AgentApp.Model);
        await SyncAsync(app, changes: false);
        app.Clock.Advance(TimeSpan.FromDays(1));
        await SyncAsync(app, changes: false);

        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));
        Assert.Equal(0L, await app.FinishedAsync(CheckJob));

        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync("ModelLifecycleJob"));
        await app.RunJobAsync("ModelLifecycleJob");

        Assert.Equal(AgentStatus.Paused, await app.StatusAsync(user.Id, agent.Id));
        Assert.Equal([$"ModelRetired /agents/{agent.Id}/settings"], await NotificationsAsync(app, user.Id));
    }

    /// <summary>A model listed again is Active, so a later deprecation is news again.</summary>
    [Fact]
    public async Task A_model_deprecated_again_after_coming_back_warns_again()
    {
        await using var app = new AgentApp(postgres);
        app.Lists(CredentialApp.AiKey, AgentApp.Model, Retiring);
        var user = await app.UserAsync();
        var agent = await ActiveAsync(app, user, new RoleAssignment(AiRole.Strategist, new ModelRef(AgentApp.Provider, Retiring)));

        app.Lists(CredentialApp.AiKey, AgentApp.Model);
        await SyncAsync(app);
        app.Lists(CredentialApp.AiKey, AgentApp.Model, Retiring);
        await SyncAsync(app);
        app.Lists(CredentialApp.AiKey, AgentApp.Model);
        await SyncAsync(app);

        Assert.Equal(AgentStatus.Active, await app.StatusAsync(user.Id, agent.Id));
        Assert.Equal(["ModelDeprecated", "ModelDeprecated"], (await NotificationsAsync(app, user.Id)).Select(row => row.Split(' ')[0]));
    }

    /// <summary>The ready settings with <paramref name="roles"/> in place of the ones they name.</summary>
    private static AgentSettings With(AgentApp.User user, string name, params RoleAssignment[] roles)
    {
        var ready = AgentApp.Ready(user, name);

        return ready with { Roles = [.. ready.Roles.Where(role => roles.All(other => other.Role != role.Role)), .. roles] };
    }

    private static async Task<AgentRecord> ActiveAsync(AgentApp app, AgentApp.User user, params RoleAssignment[] roles)
    {
        var draft = await app.CreateAsync(user.Id, With(user, "Scalper", roles));
        var result = await app.Agents.ActivateAsync(user.Id, draft.Id, Cancellation);

        Assert.Equal(ActivationOutcome.Activated, result.Outcome);

        return result.Agent!;
    }

    /// <summary>Runs the catalog's schedule, then waits for the check that a change of status starts, when there is one.</summary>
    private static async Task SyncAsync(AgentApp app, bool changes = true)
    {
        var checks = await app.FinishedAsync(CheckJob);

        Assert.Equal(JobStatus.Succeeded, await app.RunJobAsync("CatalogSyncJob"));

        if (changes)
        {
            await LoggedApp.UntilAsync(async () => await app.FinishedAsync(CheckJob) > checks);
        }
    }

    /// <summary>The user's notifications as "type link", oldest first.</summary>
    private static async Task<List<string>> NotificationsAsync(AgentApp app, Guid userId) =>
        [.. (await NotificationRows.OfAsync(app.ConnectionString, userId, Cancellation)).Select(row => $"{row.Type} {row.Link}")];
}
