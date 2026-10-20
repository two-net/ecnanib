using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.ModelSync.Services;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Keys;
using Ecnanib.Tests.Keys.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.ModelSync.Live;

/// <summary>FR-SYNC-02 on demand: key choice, provider failures as recorded outcomes and one sync per provider at a time, against a real PostgreSQL database with the providers faked.</summary>
[Trait("Category", "Live")]
public class ModelSyncRunnerLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public ModelSyncRunnerLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AManualSyncTriesTheRequestersKeyFirstAndFallsBackWhenItIsRejected()
    {
        await StartAsync();
        var first = await KeysLiveSeed.NewUserAsync(_fixture, "claude-first@example.com", Ct);
        var second = await KeysLiveSeed.NewUserAsync(_fixture, "claude-second@example.com", Ct);
        var third = await KeysLiveSeed.NewUserAsync(_fixture, "claude-third@example.com", Ct);
        var firstKey = await ModelSyncLiveSeed.NewKeyAsync(_fixture, first.Id, AiProvider.Claude, ModelSyncLiveSeed.ApiKey + "1", lastValidatedAt: LiveSeed.Now.AddHours(-2));
        var secondKey = await ModelSyncLiveSeed.NewKeyAsync(_fixture, second.Id, AiProvider.Claude, ModelSyncLiveSeed.ApiKey + "2", lastValidatedAt: LiveSeed.Now.AddHours(-1));
        await ModelSyncLiveSeed.NewKeyAsync(_fixture, third.Id, AiProvider.Claude, ModelSyncLiveSeed.ApiKey + "3", lastValidatedAt: LiveSeed.Now.AddHours(-3));
        var ai = new FakeAiClientFactory
        {
            OnListModels = (key, _) => key.KeyId == firstKey.Id.ToString("D")
                ? throw new AiAuthException("Claude list models failed: invalid x-api-key (authentication_error)", AiProvider.Claude, 401, "authentication_error")
                : Task.FromResult(FakeAiClientFactory.DefaultModels(AiProvider.Claude)),
        };
        var logs = new TestLoggerProvider();
        await using var provider = Build(logs, ai);

        var outcome = await provider.GetRequiredService<IModelSyncService>().SyncNowAsync(AiProvider.Claude, first.Id, Ct);

        Assert.Equal(ModelSyncStatus.Synced, outcome.Status);
        Assert.Equal(new CatalogSyncResult(AiProvider.Claude, 2, 0, 2, LiveSeed.Now), outcome.Result);
        // The requester's own key first, then the most recently validated one; the third is never needed.
        Assert.Equal([firstKey.Id.ToString("D"), secondKey.Id.ToString("D")], ai.Listings.Select(k => k.KeyId));
        var rejected = await ModelSyncLiveSeed.KeyAsync(_fixture, firstKey.Id);
        Assert.Equal((KeyStatus.Valid, LiveSeed.Now.AddHours(-2)), (rejected.Status, rejected.LastValidatedAt));
        var state = await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Claude);
        Assert.Equal((LiveSeed.Now, SyncStatus.Succeeded, (string?)null, 2, SyncTrigger.Manual), (state!.LastSyncAt, state.LastStatus, state.LastError, state.ModelCount, state.Trigger));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == $"Catalog sync for Claude (Manual): key {firstKey.Id} was rejected: Claude list models failed: invalid x-api-key (authentication_error)");
        AssertNoSecret(logs);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, first.Id, second.Id, third.Id);
    }

    [Fact]
    public async Task WithoutAValidKeyNothingIsCalledOrRecorded()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "gemini-invalid@example.com", Ct);
        var other = await KeysLiveSeed.NewUserAsync(_fixture, "gemini-unvalidated@example.com", Ct);
        await ModelSyncLiveSeed.NewKeyAsync(_fixture, user.Id, AiProvider.Gemini, ModelSyncLiveSeed.ApiKey, KeyStatus.Invalid);
        await ModelSyncLiveSeed.NewKeyAsync(_fixture, other.Id, AiProvider.Gemini, ModelSyncLiveSeed.ApiKey, KeyStatus.Unvalidated);
        var ai = new FakeAiClientFactory();
        var logs = new TestLoggerProvider();
        await using var provider = Build(logs, ai);

        var outcome = await provider.GetRequiredService<IModelSyncService>().SyncNowAsync(AiProvider.Gemini, user.Id, Ct);

        Assert.Equal(new ModelSyncOutcome(ModelSyncStatus.NoValidKey, AiProvider.Gemini, null, "No valid Gemini key is stored."), outcome);
        Assert.Empty(ai.Listings);
        Assert.Null(await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Gemini));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Debug && e.Message == "Catalog sync for Gemini (Manual) skipped: no valid key is stored");
        var views = await provider.GetRequiredService<IModelCatalog>().GetSyncStatesAsync(Ct);
        Assert.Equal(new ProviderSyncView(AiProvider.Gemini, null, SyncStatus.Never, null, 0, null, 0, 0), views[1]);
        AssertNoSecret(logs);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id, other.Id);
    }

    [Fact]
    public async Task AProviderFailureIsRecordedWithoutCountingMissesAndRejectionsStopAtThreeKeys()
    {
        await StartAsync();
        var time = new FakeTimeProvider(LiveSeed.Now);
        var ai = new FakeAiClientFactory();
        var logs = new TestLoggerProvider();
        await using var provider = Build(logs, ai, time);
        await provider.GetRequiredService<IProviderCatalogSync>().SyncAsync(AiProvider.Gpt, [FakeAiClientFactory.Model(AiProvider.Gpt, "gpt-old")], SyncTrigger.Scheduled, Ct);
        var users = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var user = await KeysLiveSeed.NewUserAsync(_fixture, $"gpt-{i}@example.com", Ct);
            users.Add(user.Id);
            await ModelSyncLiveSeed.NewKeyAsync(_fixture, user.Id, AiProvider.Gpt, ModelSyncLiveSeed.ApiKey + i, lastValidatedAt: LiveSeed.Now.AddMinutes(-i));
        }

        var sync = provider.GetRequiredService<IModelSyncService>();

        // An outage is recorded as the last result; no other key is tried and nothing is counted missing.
        time.Advance(TimeSpan.FromHours(1));
        ai.OnListModels = (_, _) => throw new AiUnavailableException("Gpt list models failed: gateway timeout", AiProvider.Gpt);
        var outage = await sync.SyncNowAsync(AiProvider.Gpt, users[0], Ct);

        Assert.Equal(new ModelSyncOutcome(ModelSyncStatus.Failed, AiProvider.Gpt, null, "Gpt list models failed: gateway timeout"), outage);
        Assert.Single(ai.Listings);
        var state = await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Gpt);
        Assert.Equal((LiveSeed.Now.AddHours(1), SyncStatus.Failed, "Gpt list models failed: gateway timeout", 1, SyncTrigger.Manual), (state!.LastSyncAt, state.LastStatus, state.LastError, state.ModelCount, state.Trigger));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Catalog sync for Gpt (Manual) failed: Gpt list models failed: gateway timeout");

        // Every key refused: three are tried, none is marked (re-validation owns FR-KEY-07).
        ai.OnListModels = (_, _) => throw new AiAuthException("Gpt list models failed: invalid api key", AiProvider.Gpt, 401, "invalid_api_key");
        var refused = await sync.SyncNowAsync(AiProvider.Gpt, users[0], Ct);

        Assert.Equal(ModelSyncStatus.Failed, refused.Status);
        Assert.Equal("Gpt rejected every key tried (3); last answer: Gpt list models failed: invalid api key", refused.Detail);
        Assert.Equal(4, ai.Listings.Count);
        Assert.All(await ModelSyncLiveSeed.KeysAsync(_fixture, AiProvider.Gpt), k => Assert.Equal(KeyStatus.Valid, k.Status));

        // An empty answer is not a catalog.
        ai.OnListModels = (_, _) => Task.FromResult<IReadOnlyList<AiModelInfo>>([]);
        var empty = await sync.SyncNowAsync(AiProvider.Gpt, users[0], Ct);

        Assert.Equal(new ModelSyncOutcome(ModelSyncStatus.Failed, AiProvider.Gpt, null, "Gpt listed no models; the catalog was left unchanged."), empty);
        var row = Assert.Single(await ModelSyncLiveSeed.ModelsAsync(_fixture, AiProvider.Gpt));
        Assert.Equal(("gpt-old", ModelStatus.Available, 0, LiveSeed.Now), (row.ModelId, row.Status, row.MissedSyncs, row.LastSeenAt));
        AssertNoSecret(logs);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, users.ToArray());
    }

    [Fact]
    public async Task SyncsOfOneProviderNeverInterleave()
    {
        await StartAsync();
        var user = await KeysLiveSeed.NewUserAsync(_fixture, "grok@example.com", Ct);
        await ModelSyncLiveSeed.NewKeyAsync(_fixture, user.Id, AiProvider.Grok, ModelSyncLiveSeed.ApiKey);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var overlapped = false;
        var ai = new FakeAiClientFactory
        {
            OnListModels = async (_, ct) =>
            {
                if (Interlocked.Increment(ref running) > 1)
                    overlapped = true;
                await gate.Task.WaitAsync(ct);
                Interlocked.Decrement(ref running);
                return FakeAiClientFactory.DefaultModels(AiProvider.Grok);
            },
        };
        var logs = new TestLoggerProvider();
        await using var provider = Build(logs, ai);
        var runner = provider.GetRequiredService<ModelSyncRunner>();

        var manual = runner.SyncNowAsync(AiProvider.Grok, user.Id, Ct);
        var scheduled = runner.SyncAsync(AiProvider.Grok, SyncTrigger.Scheduled, null, Ct);
        await Wait.UntilAsync(() => ai.Listings.Count == 1, "first listing");
        await Wait.SettleAsync();
        Assert.Equal(1, ai.Listings.Count);
        gate.SetResult();
        var outcomes = await Task.WhenAll(manual, scheduled);

        Assert.All(outcomes, o => Assert.Equal(ModelSyncStatus.Synced, o.Status));
        Assert.False(overlapped);
        Assert.Equal(2, ai.Listings.Count);
        Assert.Equal(2, (await ModelSyncLiveSeed.ModelsAsync(_fixture, AiProvider.Grok)).Count);
        AssertNoSecret(logs);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, user.Id);
    }

    private ServiceProvider Build(TestLoggerProvider logs, FakeAiClientFactory ai, TimeProvider? time = null) =>
        KeysTestHost.BuildServices(_fixture.ConnectionString, logs, time: time ?? new FakeTimeProvider(LiveSeed.Now), ai: ai);

    private static void AssertNoSecret(TestLoggerProvider logs) => Assert.DoesNotContain(ModelSyncLiveSeed.ApiKey, logs.AllText);

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
