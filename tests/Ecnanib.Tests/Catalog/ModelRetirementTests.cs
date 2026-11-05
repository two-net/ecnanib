using System.Collections.Concurrent;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// FR-CAT-04 and AC-09 in the catalog: a model no key lists any more is Deprecated after a sync of everyone's
/// keys, Retired after Catalog:RetireAfterMissingDays, and Active again once a key lists it. An outage, or a
/// sync of only some keys, never deprecates anything.
/// </summary>
public sealed class ModelRetirementTests(PostgresFixture postgres)
{
    private const string RetireAfter = "Catalog:RetireAfterMissingDays";

    private static CancellationToken Cancellation => CatalogApp.Cancellation;

    [Fact]
    public async Task A_model_no_key_lists_is_deprecated_then_retired_after_the_grace_period()
    {
        await using var app = new CatalogApp(postgres, new() { [RetireAfter] = "2" }, observers: false);
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        await app.AddAsync(Guid.NewGuid());
        await app.RunScheduleAsync();

        app.Lists(CatalogApp.AiKey, "model-a");
        app.Clock.Advance(TimeSpan.FromHours(12));
        await app.RunScheduleAsync();

        Assert.Equal(ModelStatus.Deprecated, await StatusAsync(app, "model-b"));
        Assert.Equal(ModelStatus.Active, await StatusAsync(app, "model-a"));
        Assert.Equal((1, 0), Counts((await app.RunsAsync())[^1]));

        app.Clock.Advance(TimeSpan.FromDays(2) - TimeSpan.FromMinutes(1));
        await app.RunScheduleAsync();

        Assert.Equal(ModelStatus.Deprecated, await StatusAsync(app, "model-b"));
        Assert.Equal((0, 0), Counts((await app.RunsAsync())[^1]));

        app.Clock.Advance(TimeSpan.FromMinutes(1));
        await app.RunScheduleAsync();
        await app.RunScheduleAsync();

        Assert.Equal(ModelStatus.Retired, await StatusAsync(app, "model-b"));
        Assert.Equal([(0, 0), (1, 0), (0, 0), (0, 1), (0, 0)], (await app.RunsAsync()).Select(Counts));
    }

    /// <summary>A key that failed keeps its rows, so a model it could use isn't missing, and neither is anything when every key failed.</summary>
    [Fact]
    public async Task An_outage_deprecates_nothing()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        app.Lists(CatalogApp.OtherAiKey, "model-a", "model-b");
        await app.AddAsync(Guid.NewGuid());
        await app.AddAsync(Guid.NewGuid(), CatalogApp.OtherAiKey);
        await app.RunScheduleAsync();

        app.Lists(CatalogApp.AiKey, "model-a");
        app.Answers(CatalogApp.OtherAiKey, () => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "overloaded_error", "busy"));
        await app.RunScheduleAsync();

        Assert.Equal(ModelStatus.Active, await StatusAsync(app, "model-b"));

        app.Answers(CatalogApp.AiKey, () => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "overloaded_error", "busy"));
        await app.RunScheduleAsync();

        Assert.Equal(ModelStatus.Active, await StatusAsync(app, "model-b"));

        app.Lists(CatalogApp.AiKey, "model-a");
        app.Lists(CatalogApp.OtherAiKey, "model-a");
        await app.RunScheduleAsync();

        Assert.Equal(ModelStatus.Deprecated, await StatusAsync(app, "model-b"));
        Assert.Equal([(0, 0), (0, 0), (0, 0), (1, 0)], (await app.RunsAsync()).Select(Counts));
    }

    /// <summary>One user's keys can't show that nobody can use a model.</summary>
    [Fact]
    public async Task A_sync_of_some_keys_deprecates_nothing()
    {
        await using var app = new CatalogApp(postgres);
        var userId = Guid.NewGuid();
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        var credential = await app.AddAsync(userId);
        await app.UntilCredentialSyncsAsync(1);

        app.Lists(CatalogApp.AiKey, "model-a");
        Assert.Equal(UserSyncOutcome.Synced, (await app.Sync.SyncUserAsync(userId, Cancellation)).Outcome);
        app.Lists(CatalogApp.OtherAiKey, "model-a");
        await app.RotateAsync(userId, credential.Id);
        await app.UntilCredentialSyncsAsync(2);

        Assert.Equal(["model-a:yes", "model-b:no"], await app.AccessAsync(userId, credential.Id));
        Assert.Equal(ModelStatus.Active, await StatusAsync(app, "model-b"));

        await app.RunScheduleAsync();

        Assert.Equal(ModelStatus.Deprecated, await StatusAsync(app, "model-b"));
    }

    /// <summary>Any key that lists a model again, on any trigger, makes it Active, which counts as an update.</summary>
    [Fact]
    public async Task A_model_listed_again_is_active_again()
    {
        await using var app = new CatalogApp(postgres, new() { [RetireAfter] = "1" }, observers: false);
        var userId = Guid.NewGuid();
        app.Lists(CatalogApp.AiKey, "model-a", "model-b", "model-c");
        await app.AddAsync(userId);
        await app.RunScheduleAsync();

        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        await app.RunScheduleAsync();
        app.Clock.Advance(TimeSpan.FromDays(1));
        app.Lists(CatalogApp.AiKey, "model-a");
        await app.RunScheduleAsync();

        Assert.Equal((ModelStatus.Deprecated, ModelStatus.Retired), (await StatusAsync(app, "model-b"), await StatusAsync(app, "model-c")));

        app.Lists(CatalogApp.AiKey, "model-a", "model-b", "model-c");
        Assert.Equal(UserSyncOutcome.Synced, (await app.Sync.SyncUserAsync(userId, Cancellation)).Outcome);

        Assert.Equal((ModelStatus.Active, ModelStatus.Active), (await StatusAsync(app, "model-b"), await StatusAsync(app, "model-c")));
        Assert.Equal(2, (await app.RunsAsync())[^1].Updated);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(app.ConnectionString, "SELECT count(*) FROM models WHERE missing_since IS NOT NULL", Cancellation));
    }

    /// <summary>
    /// Hiding is an override (FR-CAT-05) that leaves a model's lifecycle alone, so a hidden model that no key
    /// lists any more is deprecated and retired like any other, and its agents are told as usual. The pages show
    /// Hidden only for an Active one.
    /// </summary>
    [Fact]
    public async Task A_hidden_model_still_ages()
    {
        await using var app = new CatalogApp(postgres, new() { [RetireAfter] = "1" }, observers: false);
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        await app.AddAsync(Guid.NewGuid());
        await app.RunScheduleAsync();

        foreach (var model in new[] { "model-a", "model-b" })
        {
            var hidden = ModelEdit.None with { Hidden = true };
            var result = await app.Administration.SaveModelAsync(CatalogApp.AdministratorId, CatalogApp.Provider, model, hidden, Cancellation);
            Assert.Equal(ModelEditOutcome.Saved, result.Outcome);
        }

        app.Lists(CatalogApp.AiKey, "model-a");
        await app.RunScheduleAsync();

        Assert.Equal((ModelStatus.Active, ModelStatus.Deprecated), (await StatusAsync(app, "model-a"), await StatusAsync(app, "model-b")));
        Assert.Equal((1, 0), Counts((await app.RunsAsync())[^1]));

        app.Clock.Advance(TimeSpan.FromDays(1));
        await app.RunScheduleAsync();

        var models = (await app.Catalog.ListAsync(Cancellation)).Where(model => model.Provider == CatalogApp.Provider).ToList();
        Assert.All(models, model => Assert.True(model.Hidden));
        Assert.Equal([ModelStatus.Active, ModelStatus.Retired], models.Select(model => model.Status));
        Assert.Equal([ModelStatus.Hidden, ModelStatus.Retired], models.Select(model => model.ShownStatus));
        Assert.Equal(0L, await Sql.ScalarAsync<long>(app.ConnectionString, "SELECT count(*) FROM models WHERE status = 'Hidden'", Cancellation));
    }

    /// <summary>The event Agents subscribes to (Inversions in docs/architecture.md), raised once the changes have committed.</summary>
    [Fact]
    public async Task Observers_hear_each_change_once_it_has_committed()
    {
        var observer = new RecordingObserver();
        await using var app = new CatalogApp(
            postgres,
            new() { [RetireAfter] = "1" },
            observers: false,
            services: collection => collection.AddSingleton<IModelStatusObserver>(observer));
        observer.Read = model => Sql.ScalarAsync<string>(app.ConnectionString, $"SELECT status FROM models WHERE model_id = '{model}'", Cancellation);
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        await app.AddAsync(Guid.NewGuid());
        await app.RunScheduleAsync();

        app.Lists(CatalogApp.AiKey, "model-a");
        await app.RunScheduleAsync();
        await app.RunScheduleAsync();
        app.Clock.Advance(TimeSpan.FromDays(1));
        await app.RunScheduleAsync();
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        await app.RunScheduleAsync();

        Assert.Equal(
            [
                $"{CatalogApp.Provider} model-b Deprecated from Active, stored Deprecated",
                $"{CatalogApp.Provider} model-b Retired from Deprecated, stored Retired",
                $"{CatalogApp.Provider} model-b Active from Retired, stored Active",
            ],
            observer.Heard);
    }

    private static async Task<ModelStatus> StatusAsync(CatalogApp app, string modelId) =>
        (await app.Catalog.ListAsync(Cancellation)).Single(model => model.Provider == CatalogApp.Provider && model.ModelId == modelId).Status;

    private static (int Deprecated, int Retired) Counts(SyncRun run) => (run.Deprecated, run.Retired);

    /// <summary>Records each change with the status the database holds as it hears it.</summary>
    private sealed class RecordingObserver : IModelStatusObserver
    {
        private readonly ConcurrentQueue<string> _heard = new();

        public Func<string, Task<string>> Read { get; set; } = _ => Task.FromResult("");

        public IReadOnlyList<string> Heard => [.. _heard];

        public void OnChanged(IReadOnlyList<ModelStatusChange> changes)
        {
            foreach (var change in changes)
            {
                var stored = Read(change.ModelId).GetAwaiter().GetResult();
                _heard.Enqueue($"{change.Provider} {change.ModelId} {change.Status} from {change.PreviousStatus}, stored {stored}");
            }
        }
    }
}
