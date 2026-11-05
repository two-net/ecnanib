using System.Net.Http;
using Ecnanib.Kernel;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Ai;
using Ecnanib.Tests.Identity;
using Ecnanib.Tests.Notifications;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace Ecnanib.Tests.Catalog;

/// <summary>FR-CAT-02, FR-CAT-03 and FR-CAT-08: syncing the catalog from users' keys, and recording every run.</summary>
public sealed class CatalogSyncTests(PostgresFixture postgres)
{
    private static CancellationToken Cancellation => CatalogApp.Cancellation;

    /// <summary>FR-CAT-03(a).</summary>
    [Fact]
    public async Task Adding_a_key_syncs_its_models()
    {
        await using var app = new CatalogApp(postgres);
        var userId = Guid.NewGuid();
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");

        var credential = await app.AddAsync(userId);
        await app.UntilCredentialSyncsAsync(1);

        var models = (await app.Catalog.ListAsync(Cancellation)).Where(model => model.Provider == CatalogApp.Provider).ToList();
        Assert.Equal(["model-a", "model-b"], models.Select(model => model.ModelId));
        Assert.Equal(("Model model-a", ModelStatus.Active), (models[0].DisplayName, models[0].Status));
        Assert.Equal(models[0].FirstSeenAt, models[0].LastSeenAt);
        Assert.Equal(["model-a:yes", "model-b:yes"], await app.AccessAsync(userId, credential.Id));
        Assert.Equal([new SyncRun("Credential", 1, 0, 2, 0, 0, 0, true, "")], await app.RunsAsync());
    }

    /// <summary>FR-CAT-03(a): the new key's models replace what the old one could use.</summary>
    [Fact]
    public async Task Rotating_a_key_syncs_the_new_one()
    {
        await using var app = new CatalogApp(postgres);
        var userId = Guid.NewGuid();
        var credential = await app.AddAsync(userId);
        await app.UntilCredentialSyncsAsync(1);
        app.Lists(CatalogApp.OtherAiKey, "model-b");

        await app.RotateAsync(userId, credential.Id);
        await app.UntilCredentialSyncsAsync(2);

        Assert.Equal(["model-a:no", "model-b:yes"], await app.AccessAsync(userId, credential.Id));
    }

    /// <summary>
    /// A key added while its provider was down is synced once a check finds it valid. A check that leaves a key
    /// valid has just listed its models to validate it, so it doesn't list them a second time.
    /// </summary>
    [Fact]
    public async Task A_check_that_finds_a_key_valid_syncs_it_and_an_ordinary_check_does_not()
    {
        await using var app = new CatalogApp(postgres);
        var userId = Guid.NewGuid();
        app.Answers(CatalogApp.AiKey, () => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "busy", "down"));
        var credential = await app.AddAsync(userId);
        Assert.Equal(CredentialStatus.Unverified, credential.Status);

        app.Lists(CatalogApp.AiKey, "model-a");
        Assert.Equal(CredentialStatus.Valid, (await app.CheckAsync(userId, credential.Id)).Credential?.Status);
        await app.UntilCredentialSyncsAsync(1);
        Assert.Equal(["model-a:yes"], await app.AccessAsync(userId, credential.Id));

        var requests = app.Ai.WithKey(CatalogApp.AiKey).Count;
        await app.CheckAsync(userId, credential.Id);

        // The schedule's run waits on the database several times, so a sync started by the check would be done.
        await app.RunScheduleAsync();
        Assert.Equal(1, await app.FinishedAsync("catalog-sync-credential"));
        Assert.Equal(requests + 2, app.Ai.WithKey(CatalogApp.AiKey).Count);
    }

    /// <summary>FR-CAT-02: every user's keys, merged into one catalog, with what each key can use.</summary>
    [Fact]
    public async Task The_schedule_merges_every_user_s_keys_and_records_what_each_can_use()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        app.Lists(CatalogApp.OtherAiKey, "model-b", "model-c");
        app.Lists(CatalogApp.ThirdAiKey, "model-x");
        var firstKey = await app.AddAsync(first);
        var secondKey = await app.AddAsync(second, CatalogApp.OtherAiKey);
        var thirdKey = await app.AddAsync(second, CatalogApp.ThirdAiKey, CatalogApp.Second);

        Assert.Equal(JobStatus.Succeeded, await app.RunScheduleAsync());

        Assert.Equal(["model-a", "model-b", "model-c"], await app.ModelIdsAsync());
        Assert.Equal(["model-x"], await app.ModelIdsAsync(CatalogApp.Second));
        Assert.Equal(["model-a:yes", "model-b:yes"], await app.AccessAsync(first, firstKey.Id));
        Assert.Equal(["model-b:yes", "model-c:yes"], await app.AccessAsync(second, secondKey.Id));
        Assert.Equal(["model-x:yes"], await app.AccessAsync(second, thirdKey.Id));
        Assert.Equal([new SyncRun("Schedule", 2, 0, 3, 0, 0, 0, true, "")], await app.RunsAsync());
        Assert.Equal([new SyncRun("Schedule", 1, 0, 1, 0, 0, 0, true, "")], await app.RunsAsync(CatalogApp.Second));

        // A provider nobody has a key for still has its run, which tidied its rows.
        Assert.Equal([new SyncRun("Schedule", 0, 0, 0, 0, 0, 0, true, "")], await app.RunsAsync("OpenAI"));
    }

    [Fact]
    public async Task A_key_that_is_not_valid_is_never_asked()
    {
        await using var app = new CatalogApp(postgres);
        var userId = Guid.NewGuid();
        app.Answers(CatalogApp.AiKey, () => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "busy", "down"));
        await app.AddAsync(userId);
        var requests = app.Ai.WithKey(CatalogApp.AiKey).Count;

        await app.RunScheduleAsync();
        var result = await app.Sync.SyncUserAsync(userId, Cancellation);

        Assert.Equal(requests, app.Ai.WithKey(CatalogApp.AiKey).Count);
        Assert.Equal(UserSyncOutcome.NoKeys, result.Outcome);
        Assert.Equal([new SyncRun("Schedule", 0, 0, 0, 0, 0, 0, true, "")], await app.RunsAsync());
    }

    /// <summary>Deprecation is #25's (FR-CAT-04): until then the model stays in the catalog, and only the key loses it.</summary>
    [Fact]
    public async Task A_model_a_key_stops_listing_is_unavailable_to_it_and_stays_in_the_catalog()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var userId = Guid.NewGuid();
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        var credential = await app.AddAsync(userId);
        await app.RunScheduleAsync();
        var firstSeen = (await app.Catalog.ListAsync(Cancellation)).Single(model => model.ModelId == "model-a").FirstSeenAt;

        app.Clock.Advance(TimeSpan.FromHours(12));
        app.Lists(CatalogApp.AiKey, "model-a");
        await app.RunScheduleAsync();

        var models = await app.Catalog.ListAsync(Cancellation);
        var kept = models.Single(model => model.ModelId == "model-a");
        var dropped = models.Single(model => model.ModelId == "model-b");
        Assert.Equal(["model-a:yes", "model-b:no"], await app.AccessAsync(userId, credential.Id));

        // No key can use it now, so it's deprecated (FR-CAT-04): see ModelRetirementTests.
        Assert.Equal((ModelStatus.Active, ModelStatus.Deprecated), (kept.Status, dropped.Status));
        Assert.Equal(firstSeen, kept.FirstSeenAt);
        Assert.InRange(kept.LastSeenAt - dropped.LastSeenAt, TimeSpan.FromHours(12), TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
    }

    /// <summary>A detail one answer leaves out keeps its value, so "updated" counts only real changes (FR-CAT-08).</summary>
    [Fact]
    public async Task A_changed_detail_counts_as_updated_and_one_left_out_is_kept()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        await app.AddAsync(Guid.NewGuid());
        await app.RunScheduleAsync();

        app.Answers(CatalogApp.AiKey, () => FakeAiProvider.Json("""{"models":[{"id":"model-a"}],"next":null}"""));
        await app.RunScheduleAsync();
        var kept = (await app.Catalog.ListAsync(Cancellation)).Single(model => model.ModelId == "model-a").DisplayName;

        app.Answers(CatalogApp.AiKey, () => FakeAiProvider.Json("""{"models":[{"id":"model-a","name":"Renamed"}],"next":null}"""));
        await app.RunScheduleAsync();
        var renamed = (await app.Catalog.ListAsync(Cancellation)).Single(model => model.ModelId == "model-a").DisplayName;

        Assert.Equal(("Model model-a", "Renamed"), (kept, renamed));
        Assert.Equal([(1, 0), (0, 0), (0, 1)], (await app.RunsAsync()).Select(run => (run.Added, run.Updated)));
    }

    /// <summary>NFR-REL-05: one key's failure stays with it, and its run says why without the provider's words.</summary>
    [Fact]
    public async Task One_failing_key_does_not_stop_the_others_and_is_recorded_without_the_provider_s_message()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var working = await app.AddAsync(userId);
        var failing = await app.AddAsync(otherUserId, CatalogApp.OtherAiKey);
        await app.RunScheduleAsync();

        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        app.Answers(CatalogApp.OtherAiKey, () => FakeAiProvider.Error(HttpStatusCode.InternalServerError, "server_error", "your prompt was: buy everything"));
        Assert.Equal(JobStatus.Succeeded, await app.RunScheduleAsync());

        var run = (await app.RunsAsync())[^1];
        Assert.Equal(["model-a:yes", "model-b:yes"], await app.AccessAsync(userId, working.Id));
        Assert.Equal(["model-a:yes"], await app.AccessAsync(otherUserId, failing.Id));
        Assert.Equal((2, 1, true), (run.Keys, run.KeysFailed, run.Finished));
        Assert.Contains(failing.Id.ToString(), run.Errors, StringComparison.Ordinal);
        Assert.Contains("\"failure\": \"Other\"", run.Errors, StringComparison.Ordinal);
        Assert.Contains("\"statusCode\": 500", run.Errors, StringComparison.Ordinal);
        Assert.DoesNotContain("buy everything", run.Errors, StringComparison.Ordinal);
    }

    /// <summary>FR-CAT-08: the row is written before anything is asked, so even a run that's cut short is recorded.</summary>
    [Fact]
    public async Task A_run_cut_short_keeps_its_row_unfinished()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        await app.AddAsync(Guid.NewGuid());
        app.Ai.On(HttpMethod.Get, CatalogApp.ModelsPath, async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return FakeAiProvider.Models(null, "model-a");
        });

        Assert.Equal(JobStatus.TimedOut, await app.RunScheduleAsync(TimeSpan.FromSeconds(1)));

        var run = Assert.Single(await app.RunsAsync());
        Assert.Equal(("Schedule", 1, false), (run.Trigger, run.Keys, run.Finished));
    }

    /// <summary>The schedule and an administrator never sync everyone's keys at once, and neither moves the schedule.</summary>
    [Fact]
    public async Task A_second_sync_of_everyone_s_keys_is_refused_while_one_runs()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        await app.AddAsync(Guid.NewGuid());
        var release = new TaskCompletionSource();
        app.Ai.On(HttpMethod.Get, CatalogApp.ModelsPath, async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return FakeAiProvider.Models(null, "model-a");
        });

        var scheduled = app.RunScheduleAsync();
        await Logging.LoggedApp.UntilAsync(async () => (await app.RunsAsync()).Count == 1);

        Assert.Equal(SyncAllOutcome.AlreadyRunning, await app.Administration.SyncAllAsync(Cancellation));

        release.SetResult();
        Assert.Equal(JobStatus.Succeeded, await scheduled);
        Assert.Equal(SyncAllOutcome.Started, await app.Administration.SyncAllAsync(Cancellation));
        await app.UntilFinishedAsync("catalog-sync-all", 1);

        Assert.Equal(["Schedule", "Administrator"], (await app.RunsAsync()).Select(run => run.Trigger));
        Assert.Equal("catalog-sync", app.App.ManualJob("CatalogSyncJob").Name);
        Assert.DoesNotContain("catalog-sync", await app.JobNamesAsync());
    }

    /// <summary>
    /// The runner doesn't start a run when it can't record it, or once shutdown has begun. The guard is taken by
    /// the run itself, so a run that never started can't leave it held.
    /// </summary>
    [Fact]
    public async Task A_run_the_runner_refuses_does_not_block_the_next()
    {
        var runner = new RefusingRunner();
        await using var app = new CatalogApp(postgres, observers: false, services: runner.Use);
        await app.AddAsync(Guid.NewGuid());

        Assert.Equal(SyncAllOutcome.Started, await app.Administration.SyncAllAsync(Cancellation));
        Assert.Equal(1, runner.Refused);

        Assert.Equal(SyncAllOutcome.Started, await app.Administration.SyncAllAsync(Cancellation));
        await app.UntilFinishedAsync("catalog-sync-all", 1);
        Assert.True((await app.RunsAsync()).Single().Finished);
    }

    /// <summary>The real runner, except that it refuses the first sync of everyone's keys, as it does when it can't record one.</summary>
    private sealed class RefusingRunner
    {
        private int _refused;

        public int Refused => Volatile.Read(ref _refused);

        public void Use(IServiceCollection services)
        {
            var real = services.Last(service => service.ServiceType == typeof(IJobRunner));

            services.AddSingleton<IJobRunner>(provider => new Runner(this, (IJobRunner)real.ImplementationFactory!(provider)));
        }

        private sealed class Runner(RefusingRunner owner, IJobRunner real) : IJobRunner
        {
            public Task<JobStatus> RunAsync(string name, TimeSpan timeout, Func<JobContext, CancellationToken, Task> work) =>
                name == "catalog-sync-all" && Interlocked.CompareExchange(ref owner._refused, 1, 0) == 0
                    ? Task.FromResult(JobStatus.NotStarted)
                    : real.RunAsync(name, timeout, work);
        }
    }

    /// <summary>FR-CAT-03(c): a user's own sync, at most once a minute, because hub traffic never reaches the limiter.</summary>
    [Fact]
    public async Task A_user_syncs_their_own_keys_at_most_once_a_minute()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var userId = Guid.NewGuid();
        app.Lists(CatalogApp.AiKey, "model-a", "model-b");
        app.Lists(CatalogApp.OtherAiKey, "model-b", "model-c");
        await app.AddAsync(userId);
        await app.AddAsync(userId, CatalogApp.OtherAiKey);

        var first = await app.Sync.SyncUserAsync(userId, Cancellation);
        var second = await app.Sync.SyncUserAsync(userId, Cancellation);
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        var third = await app.Sync.SyncUserAsync(userId, Cancellation);

        Assert.Equal(new UserSyncResult(UserSyncOutcome.Synced, 2, 0, 3), first);
        Assert.Equal(UserSyncOutcome.TooSoon, second.Outcome);
        Assert.Equal(UserSyncOutcome.Synced, third.Outcome);
        Assert.Equal(["User", "User"], (await app.RunsAsync()).Select(run => run.Trigger));
    }

    [Fact]
    public async Task A_user_s_sync_that_does_not_finish_says_so()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var userId = Guid.NewGuid();
        await app.AddAsync(userId, provider: CatalogApp.Second);

        // A model can't be written without its provider's row, so the write fails.
        await Sql.ExecuteAsync(app.ConnectionString, $"DELETE FROM providers WHERE name = '{CatalogApp.Second}'", Cancellation);

        Assert.Equal(UserSyncOutcome.Failed, (await app.Sync.SyncUserAsync(userId, Cancellation)).Outcome);
    }

    [Fact]
    public async Task Deleting_a_key_removes_what_it_could_use()
    {
        await using var app = new CatalogApp(postgres);
        var userId = Guid.NewGuid();
        var credential = await app.AddAsync(userId);
        await app.UntilCredentialSyncsAsync(1);

        Assert.True(await app.Credentials.DeleteAsync(userId, credential.Id, Cancellation));
        await app.UntilCredentialSyncsAsync(2);

        Assert.Empty(await app.Catalog.ListAccessAsync(userId, Cancellation));
        Assert.Equal(["model-a"], await app.ModelIdsAsync());
    }

    /// <summary>The write reads the keys again, so a key deleted while its models were fetched gets no rows back.</summary>
    [Fact]
    public async Task A_key_deleted_during_a_sync_gets_no_rows()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var userId = Guid.NewGuid();
        var credential = await app.AddAsync(userId);
        app.Ai.On(HttpMethod.Get, CatalogApp.ModelsPath, async (_, token) =>
        {
            await app.Credentials.DeleteAsync(userId, credential.Id, token);
            return FakeAiProvider.Models(null, "model-a");
        });

        await app.Sync.SyncUserAsync(userId, Cancellation);

        Assert.Empty(await app.Catalog.ListAccessAsync(userId, Cancellation));
    }

    /// <summary>
    /// Events are lost with the process, so the schedule recovers them (Inversions in docs/architecture.md):
    /// with no observer, a deleted key keeps its rows, and an invalid one stays available, until the schedule runs.
    /// </summary>
    [Fact]
    public async Task The_schedule_tidies_up_after_lost_events()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var deletedOwner = Guid.NewGuid();
        var invalidOwner = Guid.NewGuid();
        var deleted = await app.AddAsync(deletedOwner);
        var invalid = await app.AddAsync(invalidOwner, CatalogApp.OtherAiKey);
        await app.RunScheduleAsync();

        await app.Credentials.DeleteAsync(deletedOwner, deleted.Id, Cancellation);
        app.Answers(CatalogApp.OtherAiKey, () => FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "revoked"));
        Assert.Equal(CredentialStatus.Invalid, (await app.CheckAsync(invalidOwner, invalid.Id)).Credential?.Status);
        Assert.Equal(["model-a:yes"], await app.AccessAsync(deletedOwner, deleted.Id));
        Assert.Equal(["model-a:yes"], await app.AccessAsync(invalidOwner, invalid.Id));

        await app.RunScheduleAsync();

        Assert.Empty(await app.AccessAsync(deletedOwner, deleted.Id));
        Assert.Equal(["model-a:no"], await app.AccessAsync(invalidOwner, invalid.Id));
    }

    /// <summary>A sync that began asking earlier is older, however late it writes, and never replaces a newer one.</summary>
    [Fact]
    public async Task A_stale_result_never_replaces_a_newer_one()
    {
        await using var app = new CatalogApp(postgres, observers: false);
        var userId = Guid.NewGuid();
        var credential = await app.AddAsync(userId);
        var release = new TaskCompletionSource();
        var calls = 0;
        app.Ai.On(HttpMethod.Get, CatalogApp.ModelsPath, async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await release.Task.WaitAsync(token);
                return FakeAiProvider.Models(null, "model-old");
            }

            return FakeAiProvider.Models(null, "model-new");
        });

        var stale = app.Sync.SyncUserAsync(userId, Cancellation);
        await Logging.LoggedApp.UntilAsync(() => ValueTask.FromResult(Volatile.Read(ref calls) == 1));
        app.Clock.Advance(TimeSpan.FromMinutes(5));
        await app.RunScheduleAsync();
        release.SetResult();

        Assert.Equal(UserSyncOutcome.Synced, (await stale).Outcome);
        Assert.Equal(["model-new:yes"], await app.AccessAsync(userId, credential.Id));
    }

    [Fact]
    public async Task A_disabled_provider_is_left_alone()
    {
        await using var app = new CatalogApp(postgres, new() { ["AiProviders:Anthropic:Enabled"] = "false" }, observers: false);
        await app.AddAsync(Guid.NewGuid());

        await app.RunScheduleAsync();

        Assert.Empty(await app.RunsAsync("Anthropic"));
        Assert.Single(await app.RunsAsync());
    }

    /// <summary>
    /// FR-NTF-01: administrators hear when a provider fails for every key, which points at the provider. A key
    /// the provider refused is that user's problem, so it never alerts anyone.
    /// </summary>
    [Fact]
    public async Task Administrators_hear_only_when_a_provider_fails_for_every_key()
    {
        var administrator = Visitor.NewEmail();
        await using var app = new CatalogApp(
            postgres,
            new() { ["Admin:AdminEmails:0"] = administrator, ["Admin:BootstrapSecret"] = new string('s', 40) },
            observers: false);
        var administratorId = await TestUsers.AddAsync(app.ConnectionString, administrator, verified: true, Cancellation);
        await app.AddAsync(Guid.NewGuid());
        await app.AddAsync(Guid.NewGuid(), CatalogApp.OtherAiKey, CatalogApp.Second);

        app.Answers(CatalogApp.AiKey, () => FakeAiProvider.Error(HttpStatusCode.ServiceUnavailable, "busy", "down"));
        app.Answers(CatalogApp.OtherAiKey, () => FakeAiProvider.Error(HttpStatusCode.Unauthorized, "authentication_error", "revoked"));
        await app.RunScheduleAsync();
        await app.Sync.SyncUserAsync(Guid.NewGuid(), Cancellation);

        var inbox = app.App.Services.GetRequiredService<INotifications>();
        var notice = Assert.Single(await inbox.ListAsync(administratorId, before: null, Cancellation));
        Assert.Equal((NotificationType.CatalogSyncFailed, CatalogApp.Provider, "/admin/catalog"), (notice.Type, notice.Subject, notice.Link));
    }
}
