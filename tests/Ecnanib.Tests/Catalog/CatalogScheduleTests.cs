using Ecnanib.AiProviderGateway;
using Ecnanib.Catalog;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// The catalog is synced on a schedule, every <c>CatalogSync:IntervalHours</c> of the
/// configuration file, as the application starts when the catalog is empty, and on demand
/// (FR-CAT-2). Each of them syncs the providers that the file holds a catalog key for, with that
/// key (FR-CAT-3).
/// </summary>
/// <remarks>The file of the tests syncs every 12 hours.</remarks>
public class CatalogScheduleTests
{
    private const string ClaudeKey = SettingsJson.CatalogKey;

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_catalog_is_synced_when_the_application_first_starts_and_every_interval_after()
    {
        var clock = new TestClock(Start);
        using var application = await WithAKeyForClaude(clock, PostgresServer.CreateDatabase()).StartedAsync();

        // The job had never run on this database, so it ran at once.
        Assert.Equal([("Scheduled", Start)], Syncs(application.Database));
        Assert.Equal(2, StoredAiModel.Of(application.Database, "claude").Count);

        // Just short of the interval, nothing more has happened.
        clock.Set(Start.AddHours(12).AddSeconds(-1));
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Single(StoredCatalogSync.All(application.Database));

        // When it has passed, the job runs again, and again an interval later.
        clock.Set(Start.AddHours(12));
        await Wait.UntilAsync(() => StoredCatalogSync.All(application.Database).Count == 2, "the sync after one interval");

        clock.Set(Start.AddHours(24));
        await Wait.UntilAsync(() => StoredCatalogSync.All(application.Database).Count == 3, "the sync after two intervals");

        Assert.Equal(
            [("Scheduled", Start), ("Scheduled", Start.AddHours(12)), ("Scheduled", Start.AddHours(24))],
            Syncs(application.Database));

        // Each time with the key of the file, and only the provider that the file has one for.
        Assert.Equal(Enumerable.Repeat(new AskedList("claude", ClaudeKey), 3), application.Providers.Asked);
        Assert.All(StoredAiModel.All(application.Database), model => Assert.Equal("claude", model.Provider));
    }

    [Fact]
    public async Task A_catalog_that_is_empty_is_synced_as_the_application_starts_and_one_that_is_not_waits_for_its_time()
    {
        var database = PostgresServer.CreateDatabase();
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.Failed) };

        // The first start finds the provider down. The job ran, and the catalog is empty.
        using (var first = await WithAKeyForClaude(new TestClock(Start), database, providers).StartedAsync())
        {
            await Wait.UntilAsync(() => StoredCatalogSync.All(database).Count == 1, "the sync that failed");
        }

        Assert.Empty(StoredAiModel.All(database));
        Assert.Equal("Succeeded", StoredJob.Of(database, CatalogApplication.Job)?.Outcome);

        // An hour later the application starts again. The job is not due for another eleven
        // hours, but the catalog is empty, so it runs now.
        providers.Answer = (_, _) => StandInModelLists.Listed(StandInModelLists.Model("claude-a"), StandInModelLists.Model("claude-b"));

        using (var second = await WithAKeyForClaude(new TestClock(Start.AddHours(1)), database, providers).StartedAsync())
        {
            await Wait.UntilAsync(() => StoredAiModel.All(database).Count == 2, "the sync as the application starts on an empty catalog");

            Assert.Equal([("Scheduled", Start), ("Scheduled", Start.AddHours(1))], Syncs(database));
            Assert.Equal(Start.AddHours(1), StoredJob.Of(database, CatalogApplication.Job)?.StartedAt);
        }

        // Another hour later it starts once more. The catalog is filled, so nothing is synced:
        // a restart does not ask the providers again.
        using (var third = await WithAKeyForClaude(new TestClock(Start.AddHours(2)), database, providers).StartedAsync())
        {
            await Wait.UntilAsync(
                () => third.SchedulerEvents().Any(written => written.Template == SchedulerLog.Active), "the background work starting");
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.Equal(2, StoredCatalogSync.All(database).Count);
            Assert.Equal(2, providers.Asked.Count);
        }

        // It is synced again when the interval has passed since the sync of the second start.
        using var fourth = await WithAKeyForClaude(new TestClock(Start.AddHours(13)), database, providers).StartedAsync();
        await Wait.UntilAsync(() => StoredCatalogSync.All(database).Count == 3, "the sync that was due while the application was down");

        Assert.Equal(("Scheduled", Start.AddHours(13)), Syncs(database)[^1]);
    }

    [Fact]
    public async Task A_provider_without_a_model_at_the_start_has_every_provider_of_the_service_synced()
    {
        // The service has a key for two providers. At the first start one of them is down.
        var database = PostgresServer.CreateDatabase();
        var providers = new StandInModelLists
        {
            Answer = (provider, _) => provider == "claude"
                ? StandInModelLists.Ended(AiModelListOutcome.Failed)
                : StandInModelLists.Listed(StandInModelLists.Model("gemini-a")),
        };

        using (var first = await WithKeysForTwo(new TestClock(Start), database, providers).StartedAsync())
        {
            await Wait.UntilAsync(() => StoredCatalogSync.All(database).Count == 2, "the syncs of the first start");
        }

        Assert.Equal(["gemini-a"], StoredAiModel.All(database).Select(model => model.Identifier));

        // An hour later the application starts again, and the provider is back. The job runs,
        // because one catalog is empty, and a run of the job syncs both: the next run is then
        // an interval after this one for both.
        providers.Answer = (provider, _) => StandInModelLists.Listed(StandInModelLists.Model($"{provider}-a"));

        using var second = await WithKeysForTwo(new TestClock(Start.AddHours(1)), database, providers).StartedAsync();
        await Wait.UntilAsync(() => StoredCatalogSync.All(database).Count == 4, "the syncs of the second start");

        Assert.Equal(["claude-a", "gemini-a"], StoredAiModel.All(database).Select(model => model.Identifier));
        Assert.Equal(
            [("claude", "Failed"), ("gemini", "Succeeded"), ("claude", "Succeeded"), ("gemini", "Succeeded")],
            StoredCatalogSync.All(database)
                .OrderBy(sync => sync.StartedAt)
                .ThenBy(sync => sync.Provider)
                .Select(sync => (sync.Provider, sync.Result)));

        static CatalogApplication WithKeysForTwo(TestClock clock, TestDatabase database, StandInModelLists providers) =>
            new(clock, database, providers, file => file.WithCatalogKey().With("AiProviders:Gemini:CatalogKey", "gemini-catalog-key"));
    }

    [Fact]
    public async Task A_sync_is_run_on_demand_for_the_providers_that_the_service_has_a_key_for()
    {
        var clock = new TestClock(Start);
        using var application = await WithAKeyForClaude(clock, PostgresServer.CreateDatabase()).StartedAsync();

        // Ten minutes after the start, long before the job is due.
        application.Providers.Answer = (_, _) => StandInModelLists.Listed(StandInModelLists.Model("claude-a"));
        clock.Set(Start.AddMinutes(10));

        var runs = await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        // The answer is the sync of each such provider, as it is recorded.
        var run = Assert.Single(runs);
        Assert.Equal(
            (new AiProviderCode("claude"), CatalogSyncCause.OnDemand, CatalogSyncResult.Succeeded, Start.AddMinutes(10)),
            (run.Provider, run.Cause, run.Result, run.StartedAt));
        Assert.Equal((1, 0, 0), (run.Added, run.Changed, run.Unavailable));
        Assert.Equal([("Scheduled", Start), ("OnDemand", Start.AddMinutes(10))], Syncs(application.Database));

        // The key of the file was used. A provider that the file has no key for was not asked.
        Assert.Equal(Enumerable.Repeat(new AskedList("claude", ClaudeKey), 2), application.Providers.Asked);

        // The schedule goes on from the job's own run: it is next due 12 hours after the start.
        clock.Set(Start.AddHours(12));
        await Wait.UntilAsync(() => StoredCatalogSync.All(application.Database).Count == 3, "the sync on the schedule");

        Assert.Equal(("Scheduled", Start.AddHours(12)), Syncs(application.Database)[^1]);
    }

    [Fact]
    public async Task Without_a_catalog_key_neither_the_job_nor_a_request_asks_a_provider()
    {
        using var application = await new CatalogApplication(new TestClock(Start)).StartedAsync();

        await application.RunJobAsync(Start);
        var runs = await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Empty(runs);
        Assert.Empty(application.Providers.Asked);
        Assert.Empty(StoredCatalogSync.All(application.Database));

        // The job ran, and said nothing: there was nothing to say.
        Assert.Equal("Succeeded", StoredJob.Of(application.Database, CatalogApplication.Job)?.Outcome);
        Assert.Empty(application.CatalogEvents());
    }

    [Fact]
    public async Task A_catalog_key_for_a_provider_whose_models_cannot_be_listed_is_said_and_syncs_nothing()
    {
        // The file holds a key for Claude and for Gemini. Nothing lists the models of Claude.
        var providers = new StandInModelLists { Providers = [new AiProviderCode("gemini")] };
        using var application = await new CatalogApplication(
            new TestClock(Start),
            providers: providers,
            settings: file => file.WithCatalogKey().With("AiProviders:Gemini:CatalogKey", "gemini-catalog-key"))
            .StartedAsync();

        // The other provider is synced all the same.
        Assert.Equal([new AskedList("gemini", "gemini-catalog-key")], providers.Asked);
        Assert.Equal(2, StoredAiModel.Of(application.Database, "gemini").Count);
        Assert.Empty(StoredAiModel.Of(application.Database, "claude"));

        var warning = Assert.Single(application.CatalogEvents(), written => written.Level == "Warning");
        Assert.Equal("claude", warning.Text("Provider"));

        // The key of the file is in no event.
        Assert.DoesNotContain(ClaudeKey, application.LogText());
    }

    private static CatalogApplication WithAKeyForClaude(TestClock clock, TestDatabase database, StandInModelLists? providers = null) =>
        new(clock, database, providers, file => file.WithCatalogKey());

    /// <summary>Why each sync ran and when it started, in order.</summary>
    private static IReadOnlyList<(string Cause, DateTimeOffset StartedAt)> Syncs(TestDatabase database) =>
        [.. StoredCatalogSync.All(database).Select(sync => (sync.Cause, sync.StartedAt))];
}
