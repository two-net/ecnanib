using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Tests.Database.Live;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Tests.ModelSync.Live;

/// <summary>Stored provider keys for the model-sync live tests, written directly so that seeding never runs a key-validation sync.</summary>
internal static class ModelSyncLiveSeed
{
    public const string ApiKey = "sk-live-SECRET-0123456789abcdefghijklmnopqrstuvwxyz";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<ProviderKey> NewKeyAsync(DatabaseLiveFixture fixture, Guid userId, AiProvider provider, string apiKey, KeyStatus status = KeyStatus.Valid, DateTimeOffset? lastValidatedAt = null)
    {
        await using var db = fixture.CreateContext();
        var key = new ProviderKey
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Provider = provider,
            ApiKey = apiKey,
            Status = status,
            LastValidatedAt = lastValidatedAt ?? (status == KeyStatus.Unvalidated ? null : LiveSeed.Now),
            LastValidationError = status == KeyStatus.Invalid ? "revoked" : null,
            CreatedAt = LiveSeed.Now,
        };
        db.Add(key);
        await db.SaveChangesAsync(Ct);
        return key;
    }

    public static async Task<ProviderKey> KeyAsync(DatabaseLiveFixture fixture, Guid keyId)
    {
        await using var db = fixture.CreateContext();
        return await db.ProviderKeys.AsNoTracking().SingleAsync(k => k.Id == keyId, Ct);
    }

    public static async Task<List<ProviderKey>> KeysAsync(DatabaseLiveFixture fixture, AiProvider provider)
    {
        await using var db = fixture.CreateContext();
        return await db.ProviderKeys.AsNoTracking().Where(k => k.Provider == provider).ToListAsync(Ct);
    }

    public static async Task<ProviderSyncState?> StateAsync(DatabaseLiveFixture fixture, AiProvider provider)
    {
        await using var db = fixture.CreateContext();
        return await db.ProviderSyncStates.AsNoTracking().SingleOrDefaultAsync(s => s.Provider == provider, Ct);
    }

    public static async Task<List<ProviderModel>> ModelsAsync(DatabaseLiveFixture fixture, AiProvider provider)
    {
        await using var db = fixture.CreateContext();
        return await db.ProviderModels.AsNoTracking().Where(m => m.Provider == provider).OrderBy(m => m.ModelId).ToListAsync(Ct);
    }
}
