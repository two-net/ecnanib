using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Ecnanib.Tests.Database.Live;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ModelSync.Live;

/// <summary>The catalog read behind the council's model picker (FR-AI-03, FR-SYNC-05): grouped by provider, available first, newest first.</summary>
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
    public async Task ListsEveryProviderAvailableFirstAndNewestFirst()
    {
        _fixture.Require();
        await _fixture.MigrateAsync();
        var now = LiveSeed.Now;
        await using (var db = _fixture.CreateContext())
        {
            db.AddRange(
                new ProviderModel { Provider = AiProvider.Gpt, ModelId = "gpt-a", DisplayName = "GPT A", FirstSeenAt = now, LastSeenAt = now },
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-old", DisplayName = "Old", ProviderCreatedAt = now.AddDays(-30), FirstSeenAt = now, LastSeenAt = now },
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-new", DisplayName = "New", ProviderCreatedAt = now.AddDays(-1), FirstSeenAt = now, LastSeenAt = now },
                new ProviderModel { Provider = AiProvider.Claude, ModelId = "claude-gone", DisplayName = "Gone", ProviderCreatedAt = now, FirstSeenAt = now, LastSeenAt = now, Status = ModelStatus.Deprecated },
                new ProviderModel { Provider = AiProvider.Gemini, ModelId = "gemini-b", DisplayName = "gemini-b", FirstSeenAt = now.AddDays(-2), LastSeenAt = now },
                new ProviderModel { Provider = AiProvider.Gemini, ModelId = "gemini-a", DisplayName = "gemini-a", FirstSeenAt = now.AddDays(-2), LastSeenAt = now });
            await db.SaveChangesAsync(Ct);
        }

        var (provider, _, _, _, _) = Ecnanib.Tests.Agents.Live.AgentsLiveSeed.Build(_fixture);
        await using var _ = provider;
        var models = await provider.GetRequiredService<IModelCatalog>().ListAsync(Ct);

        Assert.Equal(["claude-new", "claude-old", "claude-gone", "gemini-a", "gemini-b", "gpt-a"], models.Select(m => m.ModelId));
        Assert.Equal(ModelStatus.Deprecated, models.Single(m => m.ModelId == "claude-gone").Status);
        Assert.Equal("New", models[0].DisplayName);
    }
}
