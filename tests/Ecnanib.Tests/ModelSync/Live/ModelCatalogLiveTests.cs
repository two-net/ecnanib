using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.Tests.Database.Live;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ModelSync.Live;

/// <summary>The catalog reads behind the council's model picker and the providers page (FR-AI-03, FR-SYNC-05, FR-SYNC-06): grouped by provider, available first, newest first; one sync view per provider.</summary>
[Trait("Category", "Live")]
public class ModelCatalogLiveTests : IClassFixture<DatabaseLiveFixture>
{
    private readonly DatabaseLiveFixture _fixture;

    public ModelCatalogLiveTests(DatabaseLiveFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListsEveryProviderAvailableFirstAndNewestFirstAndOneSyncViewPerProvider()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var now = LiveSeed.Now;
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(
                new ProviderModel { Provider = AiProvider.Gpt, ModelId = "gpt-a", DisplayName = "GPT A", FirstSeenAt = now, LastSeenAt = now, InputTokenLimit = 400_000, OutputTokenLimit = 32_000 },
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-old", DisplayName = "Old", ProviderCreatedAt = now.AddDays(-30), FirstSeenAt = now, LastSeenAt = now },
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-new", DisplayName = "New", ProviderCreatedAt = now.AddDays(-1), FirstSeenAt = now, LastSeenAt = now.AddHours(1) },
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-gone", DisplayName = "Gone", ProviderCreatedAt = now, FirstSeenAt = now, LastSeenAt = now, Status = ModelStatus.Deprecated },
                new ProviderModel { Provider = AiProvider.Gemini, ModelId = "gemini-b", DisplayName = "gemini-b", FirstSeenAt = now.AddDays(-2), LastSeenAt = now },
                new ProviderModel { Provider = AiProvider.Gemini, ModelId = "gemini-a", DisplayName = "gemini-a", FirstSeenAt = now.AddDays(-2), LastSeenAt = now },
                new ProviderSyncState { Provider = AiProvider.Claude, LastSyncAt = now.AddHours(1), LastStatus = SyncStatus.Succeeded, ModelCount = 3, Trigger = SyncTrigger.Scheduled },
                new ProviderSyncState { Provider = AiProvider.Grok, LastSyncAt = now, LastStatus = SyncStatus.Failed, LastError = "Grok list models failed: 502", Trigger = SyncTrigger.Manual });
            await db.SaveChangesAsync(Ct);
        }

        var (provider, _, _, _, _) = Ecnanib.Tests.Agents.Live.AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var catalog = provider.GetRequiredService<IModelCatalog>();

        var models = await catalog.ListAsync(Ct);

        Assert.Equal(["claude-new", "claude-old", "claude-gone", "gemini-a", "gemini-b", "gpt-a"], models.Select(m => m.ModelId));
        Assert.Equal(ModelStatus.Deprecated, models.Single(m => m.ModelId == "claude-gone").Status);
        Assert.Equal(new CatalogModel(AiProvider.Claude, "claude-new", "New", ModelStatus.Available, now, now.AddHours(1), now.AddDays(-1), null, null), models[0]);
        Assert.Equal((400_000, 32_000), (models[^1].InputTokenLimit, models[^1].OutputTokenLimit));

        var states = await catalog.GetSyncStatesAsync(Ct);

        Assert.Equal(
        [
            new ProviderSyncView(AiProvider.Claude, now.AddHours(1), SyncStatus.Succeeded, null, 3, SyncTrigger.Scheduled, 2, 1),
            new ProviderSyncView(AiProvider.Gemini, null, SyncStatus.Never, null, 0, null, 2, 0),
            new ProviderSyncView(AiProvider.Gpt, null, SyncStatus.Never, null, 0, null, 1, 0),
            new ProviderSyncView(AiProvider.Grok, now, SyncStatus.Failed, "Grok list models failed: 502", 0, SyncTrigger.Manual, 0, 0),
        ], states);
    }
}
