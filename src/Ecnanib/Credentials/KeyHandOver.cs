using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Security;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Credentials;

/// <summary>
/// Hands a user's stored key of an AI provider to the call of a model (FR-KEY-9): it reads the
/// row of the key, and decrypts the value for the one call. See docs/credentials.md.
/// </summary>
/// <remarks>
/// It only reads. A key that its provider refused when it was last asked is not handed over: the
/// agents that run with it were moved to Attention required when it was marked (FR-KEY-10), and
/// a cycle that was under way then makes no further call with it.
/// </remarks>
internal sealed class KeyHandOver(
    IDbContextFactory<EcnanibDbContext> contexts, StoredSecrets secrets, ILogger<KeyHandOver> logger) : IKeyHandOver
{
    public async Task<HandedKey> AiKeyAsync(Guid userId, AiProviderCode provider, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // Only what the call needs: which row it is, what the last check found, and the value
        // to read the key from.
        var stored = await context.Set<AiCredential>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(credential => credential.Provider == provider)
            .Select(credential => new { credential.Id, credential.EncryptedValue, credential.MasterKeyId, credential.Status })
            .SingleOrDefaultAsync(cancellationToken);

        if (stored is null)
        {
            return HandedKey.None(KeyHandOverOutcome.NoKey);
        }

        if (stored.Status == KeyStatus.Invalid)
        {
            return HandedKey.None(KeyHandOverOutcome.Invalid);
        }

        if (StoredKeyText.Read(secrets, stored.EncryptedValue, stored.MasterKeyId, AiCredential.BoundTo(stored.Id, userId)) is not { } key)
        {
            logger.LogError(
                "The key of user {UserId} for the AI provider {Provider} cannot be read, so it is handed to no call", userId, provider.Value);

            return HandedKey.None(KeyHandOverOutcome.Unreadable);
        }

        return HandedKey.Of(key);
    }
}
