using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Security;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Credentials;

/// <summary>
/// Hands a user's stored key to the call of an agent that needs it (FR-KEY-9): the key of an AI
/// provider to the call of a model, and the Binance credential to a call about an order. It
/// reads the row of the key, and decrypts the value for the one call. See docs/credentials.md.
/// </summary>
/// <remarks>
/// <para>
/// It only reads. A key of an AI provider that its provider refused when it was last asked is
/// not handed over: the agents that run with it were moved to Attention required when it was
/// marked (FR-KEY-10), and a cycle that was under way then makes no further call with it.
/// </para>
/// <para>
/// A Binance credential is handed over whatever its last check found: a position is closed with
/// it in any state, and Binance says whether it takes the credential.
/// </para>
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

    public async Task<HandedExchangeKey> ExchangeKeyAsync(Guid userId, TradingMode environment, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var stored = await context.Set<ExchangeCredential>()
            .AsNoTracking()
            .OwnedBy(userId)
            .Where(credential => credential.Environment == environment)
            .Select(credential => new { credential.Id, credential.EncryptedValue, credential.MasterKeyId })
            .SingleOrDefaultAsync(cancellationToken);

        if (stored is null)
        {
            return HandedExchangeKey.None(KeyHandOverOutcome.NoKey);
        }

        // The two keys are stored as one value: the API key, a line feed, and the secret key.
        var keys = StoredKeyText.Read(secrets, stored.EncryptedValue, stored.MasterKeyId, ExchangeCredential.BoundTo(stored.Id, userId));
        var parting = keys?.IndexOf('\n', StringComparison.Ordinal) ?? -1;

        if (keys is null || parting <= 0 || parting == keys.Length - 1)
        {
            logger.LogError(
                "The key of user {UserId} of Binance for {TradingMode} cannot be read, so it is handed to no call", userId, environment);

            return HandedExchangeKey.None(KeyHandOverOutcome.Unreadable);
        }

        return HandedExchangeKey.Of(keys[..parting], keys[(parting + 1)..]);
    }
}
