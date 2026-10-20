using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.ModelSync.Services;
using Ecnanib.Tests.Database.Live;
using Ecnanib.Tests.Keys;
using Ecnanib.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.ModelSync.Live;

/// <summary>FR-SYNC-01, FR-SYNC-03 and FR-SYNC-06 for the catalog upsert against a real PostgreSQL database, with a deprecation threshold of two.</summary>
[Trait("Category", "Live")]
public class ProviderCatalogSyncLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public ProviderCatalogSyncLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AKeyValidationNeverCountsMissesAndAScheduledSyncDoes()
    {
        await StartAsync();
        var time = new FakeTimeProvider(LiveSeed.Now);
        var logs = new TestLoggerProvider();
        await using var provider = Build(logs, time);
        var sync = provider.GetRequiredService<IProviderCatalogSync>();
        var created = LiveSeed.Now.AddDays(-30);

        var first = await sync.SyncAsync(AiProvider.Gemini, [FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-a", "Gemini A", created), FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-b", "Gemini B")], SyncTrigger.KeyValidation, Ct);

        Assert.Equal(new CatalogSyncResult(AiProvider.Gemini, 2, 0, 2, LiveSeed.Now), first);
        await using (var db = _fixture.CreateContext())
        {
            var rows = await db.ProviderModels.Where(m => m.Provider == AiProvider.Gemini).OrderBy(m => m.ModelId).ToListAsync(Ct);
            Assert.Equal(["gemini-a", "gemini-b"], rows.Select(r => r.ModelId));
            Assert.All(rows, r => Assert.Equal((LiveSeed.Now, LiveSeed.Now, ModelStatus.Available, 0), (r.FirstSeenAt, r.LastSeenAt, r.Status, r.MissedSyncs)));
            Assert.Equal(created, rows[0].ProviderCreatedAt);
            Assert.Equal((128_000, 8192, true), (rows[0].InputTokenLimit, rows[0].OutputTokenLimit, rows[0].SupportsTextGeneration));
            var state = await db.ProviderSyncStates.SingleAsync(s => s.Provider == AiProvider.Gemini, Ct);
            Assert.Equal((LiveSeed.Now, SyncStatus.Succeeded, 2, SyncTrigger.KeyValidation), (state.LastSyncAt, state.LastStatus, state.ModelCount, state.Trigger));
            Assert.Null(state.LastError);
        }

        // A second validation lists b (renamed) and c but not a: a user's validation leaves a untouched.
        time.Advance(TimeSpan.FromHours(1));
        var second = await sync.SyncAsync(AiProvider.Gemini, [FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-b", "Gemini B v2"), FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-c", "")], SyncTrigger.KeyValidation, Ct);

        Assert.Equal(new CatalogSyncResult(AiProvider.Gemini, 1, 1, 2, LiveSeed.Now.AddHours(1)), second);
        await using (var db = _fixture.CreateContext())
        {
            var rows = await db.ProviderModels.Where(m => m.Provider == AiProvider.Gemini).ToDictionaryAsync(m => m.ModelId, Ct);
            Assert.Equal(3, rows.Count);
            Assert.Equal((LiveSeed.Now, ModelStatus.Available, 0), (rows["gemini-a"].LastSeenAt, rows["gemini-a"].Status, rows["gemini-a"].MissedSyncs));
            Assert.Equal(("Gemini B v2", LiveSeed.Now, LiveSeed.Now.AddHours(1), ModelStatus.Available, 0), (rows["gemini-b"].DisplayName, rows["gemini-b"].FirstSeenAt, rows["gemini-b"].LastSeenAt, rows["gemini-b"].Status, rows["gemini-b"].MissedSyncs));
            Assert.Equal(("gemini-c", LiveSeed.Now.AddHours(1)), (rows["gemini-c"].DisplayName, rows["gemini-c"].FirstSeenAt));
        }

        // The module's own fetch counts a's absence; one miss is below the threshold of two.
        time.Advance(TimeSpan.FromHours(1));
        var third = await sync.SyncAsync(AiProvider.Gemini, [FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-b", "Gemini B v2"), FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-c")], SyncTrigger.Scheduled, Ct);

        Assert.Equal(new CatalogSyncResult(AiProvider.Gemini, 0, 2, 2, LiveSeed.Now.AddHours(2), Missed: 1, Deprecated: 0), third);
        await using (var db = _fixture.CreateContext())
        {
            var a = await db.ProviderModels.SingleAsync(m => m.ModelId == "gemini-a", Ct);
            Assert.Equal((LiveSeed.Now, ModelStatus.Available, 1), (a.LastSeenAt, a.Status, a.MissedSyncs));
            var state = await db.ProviderSyncStates.SingleAsync(s => s.Provider == AiProvider.Gemini, Ct);
            Assert.Equal((LiveSeed.Now.AddHours(2), SyncStatus.Succeeded, 2, SyncTrigger.Scheduled), (state.LastSyncAt, state.LastStatus, state.ModelCount, state.Trigger));
        }

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Catalog sync for Gemini (Scheduled): 0 added, 2 updated, 2 listed, 1 missed, 0 deprecated");
    }

    [Fact]
    public async Task AModelMissingFromConsecutiveSyncsIsDeprecatedAtTheThresholdAndRecoversWhenListedAgain()
    {
        await StartAsync();
        var time = new FakeTimeProvider(LiveSeed.Now);
        var logs = new TestLoggerProvider();
        await using var provider = Build(logs, time);
        var sync = provider.GetRequiredService<IProviderCatalogSync>();
        var catalog = provider.GetRequiredService<IModelCatalog>();
        var a = FakeAiClientFactory.Model(AiProvider.Claude, "claude-a");
        var b = FakeAiClientFactory.Model(AiProvider.Claude, "claude-b");
        await sync.SyncAsync(AiProvider.Claude, [a, b], SyncTrigger.Scheduled, Ct);

        time.Advance(TimeSpan.FromHours(1));
        var oneMiss = await sync.SyncAsync(AiProvider.Claude, [a], SyncTrigger.Manual, Ct);

        Assert.Equal(new CatalogSyncResult(AiProvider.Claude, 0, 1, 1, LiveSeed.Now.AddHours(1), Missed: 1, Deprecated: 0), oneMiss);
        Assert.Equal((ModelStatus.Available, 1), await RowAsync("claude-b"));

        time.Advance(TimeSpan.FromHours(1));
        var deprecated = await sync.SyncAsync(AiProvider.Claude, [a], SyncTrigger.Scheduled, Ct);

        Assert.Equal(new CatalogSyncResult(AiProvider.Claude, 0, 1, 1, LiveSeed.Now.AddHours(2), Missed: 1, Deprecated: 1), deprecated);
        Assert.Equal((ModelStatus.Deprecated, 2), await RowAsync("claude-b"));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Catalog sync for Claude (Scheduled) deprecated claude-b after 2 consecutive misses");
        Assert.Equal(new ProviderSyncView(AiProvider.Claude, LiveSeed.Now.AddHours(2), SyncStatus.Succeeded, null, 1, SyncTrigger.Scheduled, 1, 1), (await catalog.GetSyncStatesAsync(Ct)).Single(v => v.Provider == AiProvider.Claude));

        // A key validation that still lacks b changes nothing; a later fetch keeps counting but reports nothing new.
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new CatalogSyncResult(AiProvider.Claude, 0, 1, 1, LiveSeed.Now.AddHours(3)), await sync.SyncAsync(AiProvider.Claude, [a], SyncTrigger.KeyValidation, Ct));
        Assert.Equal((ModelStatus.Deprecated, 2), await RowAsync("claude-b"));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new CatalogSyncResult(AiProvider.Claude, 0, 1, 1, LiveSeed.Now.AddHours(4), Missed: 1, Deprecated: 0), await sync.SyncAsync(AiProvider.Claude, [a], SyncTrigger.Scheduled, Ct));
        Assert.Equal((ModelStatus.Deprecated, 3), await RowAsync("claude-b"));

        // Listed again: available at once, misses reset.
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new CatalogSyncResult(AiProvider.Claude, 0, 2, 2, LiveSeed.Now.AddHours(5)), await sync.SyncAsync(AiProvider.Claude, [a, b], SyncTrigger.Scheduled, Ct));
        Assert.Equal((ModelStatus.Available, 0), await RowAsync("claude-b"));
        var models = (await catalog.ListAsync(Ct)).Where(m => m.Provider == AiProvider.Claude).ToList();
        Assert.Equal(["claude-a", "claude-b"], models.Select(m => m.ModelId));
        Assert.All(models, m => Assert.Equal((ModelStatus.Available, LiveSeed.Now, LiveSeed.Now.AddHours(5)), (m.Status, m.FirstSeenAt, m.LastSeenAt)));
    }

    [Fact]
    public async Task RecordFailureWritesTheLastResultAndTheNextSuccessClearsIt()
    {
        await StartAsync();
        var time = new FakeTimeProvider(LiveSeed.Now);
        await using var provider = Build(new TestLoggerProvider(), time);
        var sync = provider.GetRequiredService<IProviderCatalogSync>();

        await sync.RecordFailureAsync(AiProvider.Gpt, SyncTrigger.Scheduled, new string('x', ProviderCatalogSync.MaxErrorLength + 100), Ct);

        await using (var db = _fixture.CreateContext())
        {
            var state = await db.ProviderSyncStates.SingleAsync(s => s.Provider == AiProvider.Gpt, Ct);
            Assert.Equal((LiveSeed.Now, SyncStatus.Failed, ProviderCatalogSync.MaxErrorLength, 0, SyncTrigger.Scheduled), (state.LastSyncAt, state.LastStatus, state.LastError!.Length, state.ModelCount, state.Trigger));
            Assert.False(await db.ProviderModels.AnyAsync(m => m.Provider == AiProvider.Gpt, Ct));
        }

        time.Advance(TimeSpan.FromMinutes(5));
        await sync.SyncAsync(AiProvider.Gpt, [FakeAiClientFactory.Model(AiProvider.Gpt, "gpt-1")], SyncTrigger.Manual, Ct);

        await using (var db = _fixture.CreateContext())
        {
            var state = await db.ProviderSyncStates.SingleAsync(s => s.Provider == AiProvider.Gpt, Ct);
            Assert.Equal((LiveSeed.Now.AddMinutes(5), SyncStatus.Succeeded, (string?)null, 1, SyncTrigger.Manual), (state.LastSyncAt, state.LastStatus, state.LastError, state.ModelCount, state.Trigger));
        }
    }

    [Fact]
    public async Task DuplicatesCollapseLongIdsAreSkippedAndConcurrentSyncsBothSucceed()
    {
        await StartAsync();
        var logs = new TestLoggerProvider();
        await using var provider = Build(logs, new FakeTimeProvider(LiveSeed.Now));
        var sync = provider.GetRequiredService<IProviderCatalogSync>();
        var longId = new string('x', ProviderCatalogSync.MaxLength + 1);
        AiModelInfo[] models = [FakeAiClientFactory.Model(AiProvider.Grok, "grok-1"), FakeAiClientFactory.Model(AiProvider.Grok, "grok-1", "again"), FakeAiClientFactory.Model(AiProvider.Grok, longId), FakeAiClientFactory.Model(AiProvider.Grok, new string('d', ProviderCatalogSync.MaxLength), new string('n', ProviderCatalogSync.MaxLength + 50))];

        var results = await Task.WhenAll(sync.SyncAsync(AiProvider.Grok, models, SyncTrigger.Manual, Ct), sync.SyncAsync(AiProvider.Grok, models, SyncTrigger.Manual, Ct));

        Assert.All(results, r => Assert.Equal((2, 0, 0), (r.Listed, r.Missed, r.Deprecated)));
        // Whoever commits first adds both rows; the other, racing or not, updates them.
        Assert.Equal(2, results.Sum(r => r.Added));
        Assert.Equal(2, results.Sum(r => r.Updated));
        await using (var db = _fixture.CreateContext())
        {
            var rows = await db.ProviderModels.Where(m => m.Provider == AiProvider.Grok).OrderBy(m => m.ModelId).ToListAsync(Ct);
            Assert.Equal(2, rows.Count);
            Assert.Equal(ProviderCatalogSync.MaxLength, rows[0].DisplayName.Length);
            Assert.Equal("grok-1", rows[1].DisplayName);
            Assert.Equal(2, (await db.ProviderSyncStates.SingleAsync(s => s.Provider == AiProvider.Grok, Ct)).ModelCount);
        }

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("Catalog sync for Grok skipped a model whose id is 201 characters long", StringComparison.Ordinal));
        // EF Core logs the lost INSERT itself (parameters masked); the module logs the race at Debug and retries.
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error && e.Category.StartsWith("Ecnanib.", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Debug && e.Message == "Catalog sync for Grok lost a race on the unique index; retrying once");
    }

    private ServiceProvider Build(TestLoggerProvider logs, FakeTimeProvider time) =>
        KeysTestHost.BuildServices(_fixture.ConnectionString, logs, config: new() { ["Providers:DeprecationMissThreshold"] = "2" }, time: time);

    private async Task<(ModelStatus Status, int Misses)> RowAsync(string modelId)
    {
        await using var db = _fixture.CreateContext();
        var row = await db.ProviderModels.AsNoTracking().SingleAsync(m => m.ModelId == modelId, Ct);
        return (row.Status, row.MissedSyncs);
    }

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
