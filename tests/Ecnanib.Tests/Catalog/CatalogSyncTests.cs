using Ecnanib.AiProviderGateway;
using Ecnanib.Catalog;
using Ecnanib.Shared;
using Ecnanib.Tests.AiProviderGateway;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Settings;

namespace Ecnanib.Tests.Catalog;

/// <summary>
/// The catalog of models is filled only by syncing from each provider's own list: no model is
/// hard-coded (FR-CAT-1). Each model is stored once for its provider and identifier, with what
/// the provider says about it, and with when it was first and last seen. What the provider does
/// not say is unknown (FR-CAT-4). A model that the list lacks for long enough is marked
/// unavailable, is never deleted, and is active again when it reappears (FR-CAT-5). A sync that
/// fails for one provider leaves that provider's catalog as it was and the others alone, and
/// every sync records its time, its result, and its counts (FR-CAT-7).
/// </summary>
/// <remarks>
/// The file of the tests syncs every 12 hours and lets a model miss 3 syncs. With the service's
/// key, a model is marked unavailable by the third sync in a row that lacks it. With the keys of
/// users it must also have gone unseen for three and a half days: a key is checked daily, and
/// one key may see fewer models than another.
/// </remarks>
public class CatalogSyncTests
{
    private const string ClaudeKey = SettingsJson.CatalogKey;
    private const string GeminiKey = "gemini-catalog-key";

    // Between two hours, so that the job that checks the stored keys is not due at the start.
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 20, 0, TimeSpan.Zero);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private static readonly AiProviderCode Claude = new("claude");

    private static readonly ListedAiModel Opus = StandInModelLists.Model(
        "claude-opus-5-5",
        "Claude Opus 5.5",
        contextTokens: 1_000_000,
        outputTokens: 64_000,
        releasedAt: new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));

    private static readonly ListedAiModel Haiku = StandInModelLists.Model(
        "claude-haiku-4-5",
        "Claude Haiku 4.5",
        contextTokens: 200_000,
        outputTokens: 64_000,
        releasedAt: new DateTimeOffset(2025, 10, 15, 0, 0, 0, TimeSpan.Zero),
        deprecatedAt: new DateTimeOffset(2026, 8, 1, 12, 30, 0, 123, TimeSpan.Zero),
        retiresAt: new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_catalog_holds_no_model_until_a_provider_lists_it()
    {
        // No key of the service, and no key of a user: nobody is asked, so nothing is known.
        using var application = await new CatalogApplication().StartedAsync();

        Assert.Empty(application.Providers.Asked);
        Assert.Empty(StoredAiModel.All(application.Database));
        Assert.Empty(StoredCatalogSync.All(application.Database));
    }

    [Fact]
    public async Task A_model_is_stored_with_what_its_provider_says_about_it_and_when_it_was_seen()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists
        {
            // One that the provider says everything about, and one that it only names.
            Answer = (_, _) => StandInModelLists.Listed(Haiku, StandInModelLists.Model("claude-next")),
        };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();

        var models = StoredAiModel.All(application.Database);

        Assert.Equal(["claude-haiku-4-5", "claude-next"], models.Select(model => model.Identifier));
        Assert.All(models, model => Assert.Equal(("claude", "Active", 0, Start, Start), Seen(model)));

        // The name, the limits, and the dates, as the provider said them.
        Assert.Equal(Haiku, Said(models[0]));

        // What the provider did not say is unknown: empty, and not a guess.
        var next = models[1];
        Assert.Null(next.DisplayName);
        Assert.Null(next.ContextTokens);
        Assert.Null(next.OutputTokens);
        Assert.Null(next.ReleasedAt);
        Assert.Null(next.DeprecatedAt);
        Assert.Null(next.RetiresAt);

        // The provider that the service has a key for was asked, once, with that key.
        Assert.Equal([new AskedList("claude", ClaudeKey)], application.Providers.Asked);
    }

    [Fact]
    public async Task A_model_is_stored_once_and_changed_when_its_provider_says_something_else()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();
        var first = StoredAiModel.All(application.Database);

        // An hour on, the provider lists the same two. One has another name and a larger
        // context, and is now to be retired. A third is new.
        var retires = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var legacy = Opus with { DisplayName = "Claude Opus 5.5 (legacy)", ContextTokens = 2_000_000, RetiresAt = retires };
        providers.Answer = (_, _) => StandInModelLists.Listed(
            legacy,
            Haiku,
            StandInModelLists.Model("claude-sonnet-5-5", "Claude Sonnet 5.5"));
        clock.Set(Start.AddHours(1));

        var run = Assert.Single(await application.Catalog.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal((1, 1, 0), (run.Added, run.Changed, run.Unavailable));

        var models = StoredAiModel.All(application.Database);
        Assert.Equal(["claude-haiku-4-5", "claude-opus-5-5", "claude-sonnet-5-5"], models.Select(model => model.Identifier));

        // The row of a model that was there is the same row, with what is said now, and it was
        // first seen when it was.
        var (haiku, opus, sonnet) = (models[0], models[1], models[2]);
        Assert.Equal(first.Single(model => model.Identifier == "claude-opus-5-5").Id, opus.Id);
        Assert.Equal(legacy, Said(opus));
        Assert.Equal((Start, Start.AddHours(1)), (opus.FirstSeenAt, opus.LastSeenAt));

        // One that the provider says the same about was seen again, and nothing else.
        Assert.Equal(first.Single(model => model.Identifier == "claude-haiku-4-5") with { LastSeenAt = Start.AddHours(1) }, haiku);
        Assert.Equal((Start.AddHours(1), Start.AddHours(1)), (sonnet.FirstSeenAt, sonnet.LastSeenAt));

        // A sync that finds what is there changes nothing, and counts nothing.
        run = Assert.Single(await application.Catalog.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal((0, 0, 0), (run.Added, run.Changed, run.Unavailable));
        Assert.Equal(models, StoredAiModel.All(application.Database));
    }

    [Fact]
    public async Task A_model_that_the_list_lacks_is_marked_unavailable_after_the_syncs_it_may_miss_and_is_never_deleted()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();

        // From now on the provider lists one of the two. The job syncs every 12 hours.
        providers.Answer = (_, _) => StandInModelLists.Listed(Opus);

        for (var missed = 1; missed <= 2; missed++)
        {
            await NextScheduledSyncAsync(application, clock, sync: missed + 1);

            // Missed, and still active: it may miss 3.
            var missing = StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5");
            Assert.Equal(("claude", "Active", missed, Start, Start), Seen(missing));
            Assert.Equal(0, StoredCatalogSync.Of(application.Database, "claude")[^1].Unavailable);
        }

        // The third sync that lacks it marks it, and counts it.
        await NextScheduledSyncAsync(application, clock, sync: 4);

        var gone = StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5");
        Assert.Equal(("claude", "Unavailable", 3, Start, Start), Seen(gone));
        Assert.Equal((0, 0, 1), Counts(StoredCatalogSync.Of(application.Database, "claude")[^1]));

        // The model that is listed is untouched by all of it.
        var listed = StoredAiModel.Of(application.Database, "claude", "claude-opus-5-5");
        Assert.Equal(("claude", "Active", 0, Start, Start + (3 * Interval)), Seen(listed));

        // The log names the model that is gone.
        var said = Assert.Single(application.CatalogEvents(), written => written.Text("Model") == "claude-haiku-4-5");
        Assert.Equal(("Information", "claude"), (said.Level, said.Text("Provider")));

        // It stays in the catalog as it is, with what was known about it, however many syncs
        // follow, and is counted once: when it was marked.
        await NextScheduledSyncAsync(application, clock, sync: 5);

        Assert.Equal(gone with { MissedSyncs = 4 }, StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5"));
        Assert.Equal((0, 0, 0), Counts(StoredCatalogSync.Of(application.Database, "claude")[^1]));
        Assert.Equal(2, StoredAiModel.All(application.Database).Count);
    }

    [Fact]
    public async Task With_the_services_key_the_syncs_alone_count_however_closely_they_follow_each_other()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();

        // The provider stops listing a model, and a sync is asked for three times within the hour.
        providers.Answer = (_, _) => StandInModelLists.Listed(Opus);

        foreach (var (minutes, status, unavailable) in new[] { (10, "Active", 0), (20, "Active", 0), (30, "Unavailable", 1) })
        {
            clock.Set(Start.AddMinutes(minutes));
            var run = Assert.Single(await application.Catalog.SyncAsync(TestContext.Current.CancellationToken));

            // Every list is the view of the same key, so the third that lacks the model marks it.
            Assert.Equal(unavailable, run.Unavailable);
            Assert.Equal(status, StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5").Status);
        }
    }

    [Fact]
    public async Task With_the_keys_of_users_a_model_is_not_marked_before_no_key_has_listed_it_for_long_enough()
    {
        // No key of the service: the catalog of Claude is refreshed with the keys of users, as
        // often as they are validated. One user's key sees both models, another's sees one.
        const string wide = "the-key-of-a-user-who-sees-both";
        const string narrow = "the-key-of-a-user-who-sees-one";

        var clock = new TestClock(Start);
        var providers = new StandInModelLists
        {
            Answer = (_, key) => key == wide ? StandInModelLists.Listed(Opus, Haiku) : StandInModelLists.Listed(Opus),
        };
        using var application = await new CatalogApplication(clock, providers: providers).StartedAsync();

        await application.Catalog.KeyValidatedAsync(Claude, wide, TestContext.Current.CancellationToken);

        // The narrow key is validated five times within an hour: more syncs than a model may miss.
        for (var minutes = 10; minutes <= 50; minutes += 10)
        {
            clock.Set(Start.AddMinutes(minutes));
            await application.Catalog.KeyValidatedAsync(Claude, narrow, TestContext.Current.CancellationToken);
        }

        Assert.Equal(("claude", "Active", 5, Start, Start), Seen(StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5")));

        // The wide key is checked daily, an hour later each day. Its check of the first day and
        // of the second give no list. The narrow key goes on being validated, and the model stays.
        foreach (var hours in new[] { 25, 50, 74 })
        {
            clock.Set(Start.AddHours(hours));
            await application.Catalog.KeyValidatedAsync(Claude, narrow, TestContext.Current.CancellationToken);
        }

        Assert.Equal(("claude", "Active", 8, Start, Start), Seen(StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5")));

        // On the third day the wide key lists it again. The model was seen, and was never gone.
        clock.Set(Start.AddHours(75));
        await application.Catalog.KeyValidatedAsync(Claude, wide, TestContext.Current.CancellationToken);

        Assert.Equal(
            ("claude", "Active", 0, Start, Start.AddHours(75)),
            Seen(StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5")));

        // From then on only the narrow key is left. Just short of three and a half days without
        // the model, it is still active.
        foreach (var hours in new[] { 100, 125, 150, 158.9 })
        {
            clock.Set(Start.AddHours(hours));
            await application.Catalog.KeyValidatedAsync(Claude, narrow, TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            ("claude", "Active", 4, Start, Start.AddHours(75)),
            Seen(StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5")));

        // At three and a half days it is marked: no key has listed it through three daily checks.
        clock.Set(Start.AddHours(75 + 84));
        await application.Catalog.KeyValidatedAsync(Claude, narrow, TestContext.Current.CancellationToken);

        Assert.Equal(
            ("claude", "Unavailable", 5, Start, Start.AddHours(75)),
            Seen(StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5")));
        Assert.Equal((0, 0, 1), Counts(StoredCatalogSync.Of(application.Database, "claude")[^1]));
    }

    [Fact]
    public async Task A_sync_stores_after_another_sync_of_its_provider_and_gives_up_when_its_time_runs_out()
    {
        // A clock that shows which limit the sync was given, and lets it run out at once.
        var clock = new TimerClock();
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus) };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();
        var before = StoredAiModel.All(application.Database);

        // Another sync of the provider, as far as the database can tell: a transaction that holds
        // the lock on the provider's row, which a sync takes before it stores.
        using var other = application.Database.Open();
        var storing = other.BeginTransaction();
        Lock(other, storing);

        // The provider lists a model more. The sync has the list, and waits to store it.
        providers.Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku);
        var waiting = application.Catalog.SyncAsync(TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => WaitingForALock(application) == 1, "the sync waiting for the other one");

        Assert.False(waiting.IsCompleted);
        Assert.Equal(before, StoredAiModel.All(application.Database));

        // The other one ends, and the sync stores what it listed.
        storing.Commit();
        var stored = Assert.Single(await waiting);

        Assert.Equal((Ecnanib.Catalog.CatalogSyncResult.Succeeded, 1), (stored.Result, stored.Added));
        Assert.Equal(2, StoredAiModel.All(application.Database).Count);

        // The next sync waits behind one that does not end. It has 55 seconds in all, so that it
        // finishes within a minute (NFR-PRF-4).
        storing = other.BeginTransaction();
        Lock(other, storing);
        providers.Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku, StandInModelLists.Model("claude-next"));

        waiting = application.Catalog.SyncAsync(TestContext.Current.CancellationToken);
        await Wait.UntilAsync(() => WaitingForALock(application) == 1, "the sync waiting for the one that does not end");
        Assert.Contains(TimeSpan.FromSeconds(55), clock.Timers);

        // When they run out, it gives up: nothing of the list is stored, and the sync says why.
        clock.RunOut(TimeSpan.FromSeconds(55));
        var givenUp = Assert.Single(await waiting);

        Assert.Equal(Ecnanib.Catalog.CatalogSyncResult.Failed, givenUp.Result);
        Assert.Equal(Ecnanib.Catalog.CatalogSyncFailure.TimedOut, givenUp.Failure);

        storing.Rollback();
        Assert.Equal(2, StoredAiModel.All(application.Database).Count);
        Assert.Equal(("Failed", "TimedOut"), (StoredCatalogSync.All(application.Database)[^1].Result, StoredCatalogSync.All(application.Database)[^1].Failure));

        static void Lock(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction)
        {
            using var command = new Npgsql.NpgsqlCommand(
                "SELECT code FROM ai_providers WHERE code = 'claude' FOR NO KEY UPDATE", connection, transaction);
            command.ExecuteScalar();
        }

        static long WaitingForALock(CatalogApplication application) =>
            application.Database.Scalar<long>(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");
    }

    [Fact]
    public async Task A_model_that_reappears_is_active_again()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();

        providers.Answer = (_, _) => StandInModelLists.Listed(Opus);
        for (var sync = 2; sync <= 4; sync++)
        {
            await NextScheduledSyncAsync(application, clock, sync);
        }

        var gone = StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5");
        Assert.Equal("Unavailable", gone.Status);

        // The provider lists it again.
        providers.Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku);
        await NextScheduledSyncAsync(application, clock, sync: 5);

        // The same row, active, seen now, and counted as changed: nothing about it was lost.
        var back = StoredAiModel.Of(application.Database, "claude", "claude-haiku-4-5");
        Assert.Equal(gone with { Status = "Active", MissedSyncs = 0, LastSeenAt = Start + (4 * Interval) }, back);
        Assert.Equal((0, 1, 0), Counts(StoredCatalogSync.Of(application.Database, "claude")[^1]));
    }

    [Theory]
    [InlineData(AiModelListOutcome.AuthenticationFailed, "KeyRefused")]
    [InlineData(AiModelListOutcome.TimedOut, "TimedOut")]
    [InlineData(AiModelListOutcome.Failed, "NoList")]
    public async Task A_sync_that_fails_for_one_provider_leaves_its_catalog_as_it_was_and_the_others_alone(
        AiModelListOutcome outcome, string failure)
    {
        // The service has a key for two providers, and both list their models at the start.
        var clock = new TestClock(Start);
        var providers = new StandInModelLists();
        using var application = await new CatalogApplication(
            clock, providers: providers, settings: file => file.WithCatalogKey().With("AiProviders:Gemini:CatalogKey", GeminiKey))
            .StartedAsync();
        var before = StoredAiModel.Of(application.Database, "claude");

        // An hour on, one of them gives no list. The other lists a model more.
        providers.Answer = (provider, _) => provider == "claude"
            ? StandInModelLists.Ended(outcome)
            : StandInModelLists.Listed(
                StandInModelLists.Model("gemini-test-model"),
                StandInModelLists.Model("gemini-test-model-mini"),
                StandInModelLists.Model("gemini-test-model-new"));
        clock.Set(Start.AddHours(1));

        var runs = await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        // What was asked for comes back for both, and neither failed the other.
        var failed = Assert.Single(runs, run => run.Provider == Claude);
        Assert.Equal((CatalogSyncResult.Failed, failure), (failed.Result, failed.Failure?.ToString()));
        var synced = Assert.Single(runs, run => run.Provider == new AiProviderCode("gemini"));
        Assert.Equal((CatalogSyncResult.Succeeded, 1), (synced.Result, synced.Added));
        Assert.Null(synced.Failure);

        // The catalog of the one that failed is as it was: no model changed, and none was missed.
        Assert.Equal(before, StoredAiModel.Of(application.Database, "claude"));
        Assert.Equal(3, StoredAiModel.Of(application.Database, "gemini").Count);
        Assert.All(StoredAiModel.Of(application.Database, "gemini"), model => Assert.Equal(Start.AddHours(1), model.LastSeenAt));

        // The failure is recorded, with why, in a word of the application's own.
        Assert.Equal(
            new StoredCatalogSync("claude", "OnDemand", Start.AddHours(1), Start.AddHours(1), "Failed", failure, 0, 0, 0),
            StoredCatalogSync.Of(application.Database, "claude")[^1]);

        // And it is in the log once, as a warning that names the provider.
        var warning = Assert.Single(application.CatalogEvents(), written => written.Level is "Warning" or "Error");
        Assert.Equal(("Warning", "claude", failure), (warning.Level, warning.Text("Provider"), warning.Text("Failure")));
    }

    [Fact]
    public async Task A_list_that_cannot_be_stored_leaves_nothing_of_itself_behind_but_the_failed_sync()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus) };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();
        var before = StoredAiModel.All(application.Database);

        // A list that names one model twice, which the gateway never hands on: the second cannot
        // be stored, because a model is stored once.
        providers.Answer = (_, _) => StandInModelLists.Listed(
            Opus with { DisplayName = "Another name" },
            StandInModelLists.Model("claude-twice"),
            StandInModelLists.Model("claude-twice"));
        clock.Set(Start.AddHours(1));

        var run = Assert.Single(await application.Catalog.SyncAsync(TestContext.Current.CancellationToken));

        // Asking did not fail. The sync did, and nothing of the list is in the catalog: not the
        // model that could have been stored, and not the new name.
        Assert.Equal(CatalogSyncResult.Failed, run.Result);
        Assert.Equal(CatalogSyncFailure.Error, run.Failure);
        Assert.Equal(before, StoredAiModel.All(application.Database));
        Assert.Equal(
            new StoredCatalogSync("claude", "OnDemand", Start.AddHours(1), Start.AddHours(1), "Failed", "Error", 0, 0, 0),
            StoredCatalogSync.Of(application.Database, "claude")[^1]);

        // Why is in the log, and not in the row.
        var error = Assert.Single(application.CatalogEvents(), written => written.Level == "Error");
        Assert.Equal("claude", error.Text("Provider"));
        Assert.NotNull(error.Exception);
    }

    [Fact]
    public async Task Every_sync_records_its_time_its_result_and_its_counts()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();

        providers.Answer = (_, _) => StandInModelLists.Listed(Haiku with { DisplayName = "Claude Haiku" }, StandInModelLists.Model("claude-next"));
        clock.Set(Start.AddMinutes(30));
        await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        providers.Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.Failed);
        clock.Set(Start.AddMinutes(40));
        await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new StoredCatalogSync("claude", "Scheduled", Start, Start, "Succeeded", null, Added: 2, Changed: 0, Unavailable: 0),
                new StoredCatalogSync(
                    "claude", "OnDemand", Start.AddMinutes(30), Start.AddMinutes(30), "Succeeded", null, Added: 1, Changed: 1, Unavailable: 0),
                new StoredCatalogSync(
                    "claude", "OnDemand", Start.AddMinutes(40), Start.AddMinutes(40), "Failed", "NoList", Added: 0, Changed: 0, Unavailable: 0),
            ],
            StoredCatalogSync.All(application.Database));

        // A sync that worked says so in the log, with what it counted.
        var synced = application.CatalogEvents().Where(written => written.Level == "Information" && written.Has("Added")).ToList();
        Assert.Equal(
            ["claude 2 2 0 0", "claude 2 1 1 0"],
            synced.Select(written =>
                $"{written.Text("Provider")} {written["ModelCount"].GetInt32()} {written["Added"].GetInt32()} " +
                $"{written["Changed"].GetInt32()} {written["Unavailable"].GetInt32()}"));
    }

    [Fact]
    public async Task The_status_says_the_latest_sync_of_every_provider_and_the_latest_one_that_succeeded()
    {
        var clock = new TestClock(Start);
        var providers = new StandInModelLists { Answer = (_, _) => StandInModelLists.Listed(Opus, Haiku) };
        using var application = await WithAKeyForClaude(clock, providers).StartedAsync();

        // What an operator status screen shows (FR-OPS-2): every provider of the application, by
        // its code, also the ones that were never synced.
        var status = await application.Catalog.StatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [("claude", "Claude", true), ("gemini", "Gemini", false), ("gpt", "GPT", false), ("grok", "Grok", false)],
            status.Select(provider => (provider.Provider.Value, provider.ProviderName, provider.HasCatalogKey)));

        // The sync of the start succeeded, so it is both the latest and what the catalog holds.
        var claude = status[0];
        Assert.NotNull(claude.Latest);
        Assert.Same(claude.Latest, claude.LatestSucceeded);
        Assert.Equal(
            (CatalogSyncCause.Scheduled, CatalogSyncResult.Succeeded, Start, Start, 2, 0, 0),
            (claude.Latest.Cause, claude.Latest.Result, claude.Latest.StartedAt, claude.Latest.FinishedAt, claude.Latest.Added,
                claude.Latest.Changed, claude.Latest.Unavailable));
        Assert.Null(claude.Latest.Failure);

        // A provider that the file holds no key for is synced when it takes the key of a user,
        // and none has: it has no sync.
        Assert.All(status.Skip(1), provider => Assert.Equal((null, null), (provider.Latest, provider.LatestSucceeded)));

        // Half an hour on, a sync fails. It is the latest, and the one before it is what the
        // catalog still holds.
        providers.Answer = (_, _) => StandInModelLists.Ended(AiModelListOutcome.TimedOut);
        clock.Set(Start.AddMinutes(30));
        await application.Catalog.SyncAsync(TestContext.Current.CancellationToken);

        claude = (await application.Catalog.StatusAsync(TestContext.Current.CancellationToken))[0];

        Assert.Equal(
            (CatalogSyncCause.OnDemand, CatalogSyncResult.Failed, CatalogSyncFailure.TimedOut, Start.AddMinutes(30)),
            (claude.Latest!.Cause, claude.Latest.Result, claude.Latest.Failure, claude.Latest.StartedAt));
        Assert.Equal(
            (CatalogSyncCause.Scheduled, CatalogSyncResult.Succeeded, Start),
            (claude.LatestSucceeded!.Cause, claude.LatestSucceeded.Result, claude.LatestSucceeded.StartedAt));

        // It read the syncs that are recorded, and asked no provider: the two syncs did.
        Assert.Equal(2, providers.Asked.Count);
    }

    [Fact]
    public async Task The_status_says_a_key_for_a_provider_whose_models_the_application_cannot_list_as_none()
    {
        // The file holds a key for Claude, and the application has no way to list its models:
        // nothing is synced with it, so the status does not say that something is.
        var providers = new StandInModelLists { Providers = [new AiProviderCode("gemini")] };
        using var application = await WithAKeyForClaude(new TestClock(Start), providers).StartedAsync();

        var status = await application.Catalog.StatusAsync(TestContext.Current.CancellationToken);

        Assert.All(status, provider => Assert.False(provider.HasCatalogKey, $"{provider.Provider} is said to be synced with a key of the service."));
        Assert.Empty(providers.Asked);
    }

    /// <summary>The application with a key of the service for the models of Claude, and of no other provider.</summary>
    private static CatalogApplication WithAKeyForClaude(TimeProvider clock, StandInModelLists providers) =>
        new(clock, providers: providers, settings: file => file.WithCatalogKey());

    /// <summary>
    /// Lets the next interval pass, and waits for the sync that the job then runs: the sync of
    /// this number, of the provider's syncs since the application first started.
    /// </summary>
    private static async Task NextScheduledSyncAsync(CatalogApplication application, TestClock clock, int sync)
    {
        clock.Set(Start + ((sync - 1) * Interval));

        await Wait.UntilAsync(
            () => StoredCatalogSync.Of(application.Database, "claude").Count == sync, $"sync {sync} of the catalog, on the schedule");

        Assert.Equal(
            ("Scheduled", Start + ((sync - 1) * Interval)),
            (StoredCatalogSync.Of(application.Database, "claude")[^1].Cause, StoredCatalogSync.Of(application.Database, "claude")[^1].StartedAt));
    }

    private static (string Provider, string Status, int MissedSyncs, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt) Seen(
        StoredAiModel model) =>
        (model.Provider, model.Status, model.MissedSyncs, model.FirstSeenAt, model.LastSeenAt);

    /// <summary>What a row of the catalog holds of what a provider says about a model.</summary>
    private static ListedAiModel Said(StoredAiModel model) =>
        new(
            model.Identifier,
            model.DisplayName,
            model.ContextTokens,
            model.OutputTokens,
            model.ReleasedAt,
            model.DeprecatedAt,
            model.RetiresAt);

    private static (int Added, int Changed, int Unavailable) Counts(StoredCatalogSync sync) =>
        (sync.Added, sync.Changed, sync.Unavailable);
}
