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

/// <summary>FR-SYNC-01 and FR-SYNC-06 for the catalog upsert against a real PostgreSQL database.</summary>
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
    public async Task TheFirstSyncAddsTheSecondUpdatesAndAbsentModelsAreLeftAlone()
    {
        await StartAsync();
        var time = new FakeTimeProvider(LiveSeed.Now);
        var logs = new TestLoggerProvider();
        await using var provider = KeysTestHost.BuildServices(_fixture.ConnectionString, logs, time: time);
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

            // Simulate what the scheduled sync will do to a model that went missing, then list it again.
            await db.ProviderModels.Where(m => m.ModelId == "gemini-b").ExecuteUpdateAsync(s => s.SetProperty(m => m.MissedSyncs, 2).SetProperty(m => m.Status, ModelStatus.Deprecated), Ct);
        }

        time.Advance(TimeSpan.FromHours(1));
        var second = await sync.SyncAsync(AiProvider.Gemini, [FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-b", "Gemini B v2"), FakeAiClientFactory.Model(AiProvider.Gemini, "gemini-c", "")], SyncTrigger.Scheduled, Ct);

        Assert.Equal(new CatalogSyncResult(AiProvider.Gemini, 1, 1, 2, LiveSeed.Now.AddHours(1)), second);
        await using (var db = _fixture.CreateContext())
        {
            var rows = await db.ProviderModels.Where(m => m.Provider == AiProvider.Gemini).OrderBy(m => m.ModelId).ToDictionaryAsync(m => m.ModelId, Ct);
            Assert.Equal(3, rows.Count);
            Assert.Equal(LiveSeed.Now, rows["gemini-a"].LastSeenAt);
            Assert.Equal(("Gemini B v2", LiveSeed.Now, LiveSeed.Now.AddHours(1), ModelStatus.Available, 0), (rows["gemini-b"].DisplayName, rows["gemini-b"].FirstSeenAt, rows["gemini-b"].LastSeenAt, rows["gemini-b"].Status, rows["gemini-b"].MissedSyncs));
            Assert.Equal(("gemini-c", LiveSeed.Now.AddHours(1)), (rows["gemini-c"].DisplayName, rows["gemini-c"].FirstSeenAt));
            var state = await db.ProviderSyncStates.SingleAsync(s => s.Provider == AiProvider.Gemini, Ct);
            Assert.Equal((LiveSeed.Now.AddHours(1), SyncStatus.Succeeded, 2, SyncTrigger.Scheduled), (state.LastSyncAt, state.LastStatus, state.ModelCount, state.Trigger));
        }

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message == "Catalog sync for Gemini (Scheduled): 1 added, 1 updated, 2 listed");
    }

    [Fact]
    public async Task DuplicatesCollapseLongIdsAreSkippedAndConcurrentSyncsBothSucceed()
    {
        await StartAsync();
        var logs = new TestLoggerProvider();
        await using var provider = KeysTestHost.BuildServices(_fixture.ConnectionString, logs, time: new FakeTimeProvider(LiveSeed.Now));
        var sync = provider.GetRequiredService<IProviderCatalogSync>();
        var longId = new string('x', ProviderCatalogSync.MaxLength + 1);
        AiModelInfo[] models = [FakeAiClientFactory.Model(AiProvider.Grok, "grok-1"), FakeAiClientFactory.Model(AiProvider.Grok, "grok-1", "again"), FakeAiClientFactory.Model(AiProvider.Grok, longId), FakeAiClientFactory.Model(AiProvider.Grok, new string('d', ProviderCatalogSync.MaxLength), new string('n', ProviderCatalogSync.MaxLength + 50))];

        var results = await Task.WhenAll(sync.SyncAsync(AiProvider.Grok, models, SyncTrigger.Manual, Ct), sync.SyncAsync(AiProvider.Grok, models, SyncTrigger.Manual, Ct));

        Assert.All(results, r => Assert.Equal(2, r.Listed));
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

    private async Task StartAsync()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
    }
}
