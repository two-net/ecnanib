using System.Collections.Concurrent;
using Ecnanib.AiProviderGateway;
using Ecnanib.Catalog;
using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// When a model assigned to an agent becomes unavailable, the agent moves to Attention required,
/// and the system does not substitute another model on its own (FR-CAT-8). The Catalog may not
/// use Agents, so it tells whatever depends on a model through <see cref="IModelDependents"/>,
/// inside the transaction that marks the model. No agent is given a model yet, so a stand-in of
/// the tests is told for them, as Agents will be.
/// </summary>
/// <remarks>
/// The file of the tests lets a model miss 3 syncs: the third sync in a row that lacks a model
/// marks it unavailable.
/// </remarks>
public class ModelDependentsTests
{
    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 9, 20, 0, TimeSpan.Zero);

    private static readonly ListedAiModel Opus = StandInModelLists.Model("claude-opus-5-5", "Claude Opus 5.5");
    private static readonly ListedAiModel Haiku = StandInModelLists.Model("claude-haiku-4-5", "Claude Haiku 4.5");

    [Fact]
    public async Task What_depends_on_a_model_is_told_in_the_transaction_that_marks_it_unavailable()
    {
        var clock = new TestClock(Start);
        var dependents = new StandInDependents();
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers, dependents).StartedAsync();
        var haiku = StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5");

        // The provider stops listing one of the two. A model that is only missed is still
        // active, and an agent goes on with it: nothing is told.
        providers.Answer = (_, _) => StandInModelLists.Listed(Opus);
        await SyncAsync(application, clock, minutes: 10);
        await SyncAsync(application, clock, minutes: 20);

        Assert.Empty(dependents.Told);

        // The sync that marks it tells it, with the provider and the id of the model in the
        // catalog. It is told with the context that holds the transaction, while the model is
        // still active: what it changes is stored when the model is marked, and not otherwise.
        var run = await SyncAsync(application, clock, minutes: 30);

        Assert.Equal(1, run.Unavailable);
        Assert.Equal([new Told("claude", [haiku.Id], InTransaction: true, StillActive: true)], dependents.Told, Told.Same);
        Assert.Equal("Unavailable", StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5").Status);

        // What the stand-in changed when it was told is stored with that: the mark it left on
        // the model that is gone.
        Assert.Equal(StandInDependents.Mark, StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5").DisplayName);

        // The model that the provider lists is as it was. Nothing was told about it, and
        // nothing was put in the place of the one that is gone.
        Assert.Equal("Claude Opus 5.5", StoredAiModel.Of(application.Database, "claude", "claude-opus-5-5").DisplayName);
        Assert.Equal(2, StoredAiModel.All(application.Database).Count);
    }

    [Fact]
    public async Task It_is_told_once_and_not_while_the_model_stays_unavailable_or_when_it_is_active_again()
    {
        var clock = new TestClock(Start);
        var dependents = new StandInDependents();
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers, dependents).StartedAsync();

        providers.Answer = (_, _) => StandInModelLists.Listed(Opus);
        foreach (var minutes in new[] { 10, 20, 30 })
        {
            await SyncAsync(application, clock, minutes);
        }

        Assert.Single(dependents.Told);

        // The syncs that follow lack it still. It is unavailable, as what depends on it knows.
        await SyncAsync(application, clock, minutes: 40);
        await SyncAsync(application, clock, minutes: 50);

        Assert.Single(dependents.Told);

        // The provider lists it again. It is active again, which is nothing that an agent must
        // be stopped for: its user decides what the agent does now.
        providers.Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku);
        await SyncAsync(application, clock, minutes: 60);

        Assert.Equal("Active", StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5").Status);
        Assert.Single(dependents.Told);

        // And when it goes once more, that is told once more. Both of the provider's models go
        // this time, in one sync, and are told together.
        providers.Answer = (_, _) => StandInModelLists.Listed(StandInModelLists.Model("claude-next"));
        foreach (var minutes in new[] { 70, 80, 90 })
        {
            await SyncAsync(application, clock, minutes);
        }

        Assert.Equal(2, dependents.Told.Count);
        Assert.Equal(
            StoredAiModel.Of(application.Database, "claude").Where(model => model.Status == "Unavailable").Select(model => model.Id).Order(),
            dependents.Told[1].ModelIds.Order());
        Assert.Equal(2, dependents.Told[1].ModelIds.Count);
    }

    [Fact]
    public async Task A_model_is_not_marked_unavailable_when_what_depends_on_it_could_not_be_told()
    {
        // Two that depend on a model: the first changes what it keeps, and the second fails.
        var clock = new TestClock(Start);
        var first = new StandInDependents();
        var dependents = new StandInDependents { Fails = true };
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers, first, dependents).StartedAsync();

        providers.Answer = (_, _) => StandInModelLists.Listed(Opus, StandInModelLists.Model("claude-next"));
        await SyncAsync(application, clock, minutes: 10);
        await SyncAsync(application, clock, minutes: 20);
        var before = StoredAiModel.All(application.Database);

        // An agent must not be left running on a model that is taken for gone. So when the
        // change to the agents fails, the sync fails: it is recorded as one that failed, and
        // the catalog is as it was.
        var run = await SyncAsync(application, clock, minutes: 30);

        Assert.Equal(CatalogSyncResult.Failed, run.Result);
        Assert.Equal(CatalogSyncFailure.Error, run.Failure);
        Assert.Equal(0, run.Unavailable);

        var recorded = StoredCatalogSync.Of(application.Database, "claude")[^1];
        Assert.Equal(("Failed", "Error", 0), (recorded.Result, recorded.Failure, recorded.Unavailable));

        // Both were told. What the first one changed is undone with the rest: it was told inside
        // the transaction of the sync, and not in one of its own. So every row of the catalog is
        // as it was, the model that was missed twice included.
        Assert.Single(first.Told);
        Assert.Single(dependents.Told);
        Assert.Equal(before, StoredAiModel.All(application.Database));
        Assert.Equal(("Active", 2, "Claude Haiku 4.5"), Kept(StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5")));

        // The log says that the sync failed, as an error of the application.
        Assert.Contains(application.CatalogEvents(), written => written.Level == "Error" && written.Text("Provider") == "claude");

        // Once it can be told, the next sync marks the model, and tells both again.
        dependents.Fails = false;
        run = await SyncAsync(application, clock, minutes: 40);

        Assert.Equal(CatalogSyncResult.Succeeded, run.Result);
        Assert.Equal(1, run.Unavailable);
        Assert.Equal(2, first.Told.Count);
        Assert.Equal(2, dependents.Told.Count);
        Assert.Equal(
            ("Unavailable", 3, StandInDependents.Mark), Kept(StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5")));
    }

    [Fact]
    public void Once_an_agent_uses_a_model_the_component_that_keeps_agents_is_told_when_the_model_becomes_unavailable()
    {
        using var application = new EcnanibApplicationFactory();
        using var model = ModelOnly.Context();

        // Agents keeps the settings of agents, and nothing of it is told. No agent uses a model
        // yet: no row of Agents points at one. As soon as one does, Agents must be told: the
        // Catalog cannot look at agents itself.
        var modelChoices = AgentsRules.ModelChoices(model.Model);
        var isTold = application.Services.GetServices<IModelDependents>().Any(dependent => AgentsRules.IsOfAgents(dependent.GetType()));

        Assert.True(
            isTold || modelChoices.Count == 0,
            $"An agent uses a model now ({string.Join(", ", modelChoices)}), and nothing of Agents is told when a model that " +
            "an agent uses becomes unavailable. Such an agent moves to Attention required, and no other model is put in the " +
            $"place of its own (FR-CAT-8). Implement {nameof(IModelDependents)} in Ecnanib.Agents and register it in " +
            "AddAgents(). See docs/catalog.md.");
    }

    private static CatalogApplication WithAKeyForClaude(
        TimeProvider clock, StandInModelLists providers, params StandInDependents[] dependents) =>
        new(
            clock,
            providers: providers,
            settings: file => file.WithCatalogKey(),
            services: services =>
            {
                foreach (var dependent in dependents)
                {
                    services.AddSingleton<IModelDependents>(dependent);
                }
            });

    /// <summary>Syncs the catalog on demand, so many minutes after the application started.</summary>
    private static async Task<CatalogSyncRun> SyncAsync(CatalogApplication application, TestClock clock, int minutes)
    {
        clock.Set(Start.AddMinutes(minutes));

        return Assert.Single(await application.Catalog.SyncAsync(TestContext.Current.CancellationToken));
    }

    private static (string Status, int MissedSyncs, string? DisplayName) Kept(StoredAiModel model) =>
        (model.Status, model.MissedSyncs, model.DisplayName);

    /// <summary>One time that <see cref="StandInDependents"/> was told, with how.</summary>
    /// <param name="Provider">The code of the provider whose models go.</param>
    /// <param name="ModelIds">The ids of the models in the catalog.</param>
    /// <param name="InTransaction">Whether the context it was told with holds a transaction.</param>
    /// <param name="StillActive">Whether that context still finds every one of the models active.</param>
    private sealed record Told(string Provider, IReadOnlyList<Guid> ModelIds, bool InTransaction, bool StillActive)
    {
        /// <summary>Compares two of them by what they hold, the ids one by one.</summary>
        public static IEqualityComparer<Told> Same { get; } = EqualityComparer<Told>.Create(
            (one, other) => one is not null
                && other is not null
                && (one.Provider, one.InTransaction, one.StillActive) == (other.Provider, other.InTransaction, other.StillActive)
                && one.ModelIds.SequenceEqual(other.ModelIds),
            told => told.Provider.GetHashCode(StringComparison.Ordinal));
    }

    /// <summary>
    /// Stands for Agents, which is not built yet: it is told when models become unavailable,
    /// keeps how it was told, and fails when a test tells it to. As Agents will change the state
    /// of an agent, it changes something with the context it is given: it leaves a mark on the
    /// models that go, where a test can see whether the change was stored.
    /// </summary>
    private sealed class StandInDependents : IModelDependents
    {
        /// <summary>What it writes in place of the name of a model that goes.</summary>
        public const string Mark = "told";

        private readonly ConcurrentQueue<Told> _told = new();

        /// <summary>Whether it fails, as a change to the agents that cannot be stored would.</summary>
        public bool Fails { get; set; }

        /// <summary>The times it was told, in order.</summary>
        public IReadOnlyList<Told> Told => [.. _told];

        public async Task ModelsUnavailableAsync(
            EcnanibDbContext context, AiProviderCode provider, IReadOnlyList<Guid> modelIds, CancellationToken cancellationToken)
        {
            var active = await context.Set<AiModel>()
                .CountAsync(model => modelIds.Contains(model.Id) && model.Status == AiModelStatus.Active, cancellationToken);

            await context.Set<AiModel>()
                .Where(model => modelIds.Contains(model.Id))
                .ExecuteUpdateAsync(row => row.SetProperty(model => model.DisplayName, Mark), cancellationToken);

            _told.Enqueue(new Told(provider.Value, [.. modelIds], context.Database.CurrentTransaction is not null, active == modelIds.Count));

            if (Fails)
            {
                throw new InvalidOperationException("The agents of the test could not be changed.");
            }
        }
    }
}
