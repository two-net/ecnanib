using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Services;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Keys;
using Ecnanib.Tests.Keys.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.ModelSync.Live;

/// <summary>One pass of the scheduled sync (FR-SYNC-02) over stored keys of every status, against a real PostgreSQL database with the providers faked.</summary>
[Trait("Category", "Live")]
public class ModelSyncSchedulerLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public ModelSyncSchedulerLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnePassSyncsEveryProviderWithAValidKeyAndSkipsTheOthers()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var claudeUser = await KeysLiveSeed.NewUserAsync(_fixture, "sched-claude@example.com", Ct);
        var geminiUser = await KeysLiveSeed.NewUserAsync(_fixture, "sched-gemini@example.com", Ct);
        var gptUser = await KeysLiveSeed.NewUserAsync(_fixture, "sched-gpt@example.com", Ct);
        var claudeKey = await ModelSyncLiveSeed.NewKeyAsync(_fixture, claudeUser.Id, AiProvider.Claude, ModelSyncLiveSeed.ApiKey + "c", lastValidatedAt: LiveSeed.Now.AddHours(-5));
        var geminiKey = await ModelSyncLiveSeed.NewKeyAsync(_fixture, geminiUser.Id, AiProvider.Gemini, ModelSyncLiveSeed.ApiKey + "g");
        await ModelSyncLiveSeed.NewKeyAsync(_fixture, gptUser.Id, AiProvider.Gpt, ModelSyncLiveSeed.ApiKey + "p", KeyStatus.Invalid);
        var ai = new FakeAiClientFactory
        {
            OnListModels = (key, _) => key.Provider == AiProvider.Gemini
                ? throw new AiUnavailableException("Gemini list models failed: 503 Service Unavailable", AiProvider.Gemini)
                : Task.FromResult(FakeAiClientFactory.DefaultModels(key.Provider)),
        };
        var logs = new TestLoggerProvider();
        await using var provider = KeysTestHost.BuildServices(_fixture.ConnectionString, logs, time: new FakeTimeProvider(LiveSeed.Now), ai: ai);
        var scheduler = provider.GetRequiredService<ModelSyncScheduler>();

        var first = await scheduler.SyncOnceAsync(Ct);

        Assert.Equal(new ModelSyncRunResult(1, 2, 1, 0), first);
        Assert.Equal([claudeKey.Id.ToString("D"), geminiKey.Id.ToString("D")], ai.Listings.Select(k => k.KeyId));
        var claude = await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Claude);
        Assert.Equal((LiveSeed.Now, SyncStatus.Succeeded, (string?)null, 2, SyncTrigger.Scheduled), (claude!.LastSyncAt, claude.LastStatus, claude.LastError, claude.ModelCount, claude.Trigger));
        var gemini = await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Gemini);
        Assert.Equal((SyncStatus.Failed, "Gemini list models failed: 503 Service Unavailable", 0, SyncTrigger.Scheduled), (gemini!.LastStatus, gemini.LastError, gemini.ModelCount, gemini.Trigger));
        Assert.Null(await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Gpt));
        Assert.Null(await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Grok));
        // The scheduler never validates: the key's own record is untouched.
        Assert.Equal(LiveSeed.Now.AddHours(-5), (await ModelSyncLiveSeed.KeyAsync(_fixture, claudeKey.Id)).LastValidatedAt);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message == "Catalog sync for Gemini (Scheduled) failed: Gemini list models failed: 503 Service Unavailable");
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Debug && e.Message == "Catalog sync for Gpt (Scheduled) skipped: no valid key is stored");
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Scheduled model sync checked 4 provider(s): 1 synced, 2 without a valid key, 1 failed, 0 failure(s)");

        ai.OnListModels = null;
        var second = await scheduler.SyncOnceAsync(Ct);

        Assert.Equal(new ModelSyncRunResult(2, 2, 0, 0), second);
        Assert.Equal((SyncStatus.Succeeded, (string?)null, 2), ((await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Gemini))!.LastStatus, (await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Gemini))!.LastError, (await ModelSyncLiveSeed.StateAsync(_fixture, AiProvider.Gemini))!.ModelCount));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Debug && e.Message == "Scheduled model sync checked 4 provider(s): 2 synced, 2 without a valid key, 0 failed, 0 failure(s)");
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.DoesNotContain(ModelSyncLiveSeed.ApiKey, logs.AllText);
        await KeysLiveSeed.DeleteUsersAsync(_fixture, Ct, claudeUser.Id, geminiUser.Id, gptUser.Id);
    }
}
