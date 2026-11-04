using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.ExchangeAdapter.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// Hands a valid key to the module that uses it, decrypted for that one use (FR-KEY-09), and refuses a
/// Binance key on a market Binance didn't enable for it (FR-KEY-05). See Credentials in docs/architecture.md.
/// </summary>
internal sealed class CredentialKeys(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ISecretStore secrets,
    TimeProvider time) : ICredentialKeys
{
    /// <summary>How stale <c>last_used_at</c> may get, so that a busy key doesn't write on every call.</summary>
    public static readonly TimeSpan LastUseResolution = TimeSpan.FromMinutes(1);

    public Task<AiKey> OpenAiKeyAsync(Guid userId, Guid credentialId, CancellationToken cancellationToken) =>
        OpenAiKeyAsync(userId, credentialId, use: true, cancellationToken);

    public Task<AiKey> OpenAiKeyToListModelsAsync(Guid userId, Guid credentialId, CancellationToken cancellationToken) =>
        OpenAiKeyAsync(userId, credentialId, use: false, cancellationToken);

    public async Task<IReadOnlyList<AiCredential>> ListAiAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await AiCredentials(AiRows(context).Where(row => row.UserId == userId)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiCredential>> ListAllAiAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await AiCredentials(AiRows(context)).ToListAsync(cancellationToken);
    }

    public async Task<ExchangeKey> OpenExchangeKeyAsync(Guid userId, Guid credentialId, Market market, CancellationToken cancellationToken)
    {
        var row = await UsableAsync(userId, credentialId, CredentialKind.Exchange, use: true, cancellationToken);
        var enabled = market == Market.Spot ? row.SpotEnabled : row.FuturesEnabled;

        if (enabled != true)
        {
            throw new CredentialUnusableException(credentialId, CredentialUnusableReason.MarketNotEnabled);
        }

        var apiKey = await secrets.RevealAsync(row.ApiKeyId, cancellationToken);

        try
        {
            var secret = await secrets.RevealAsync(
                row.ApiSecretId ?? throw new InvalidOperationException($"Credential {row.Id} has no secret."), cancellationToken);

            return new ExchangeKey(
                row.Id,
                row.Environment ?? throw new InvalidOperationException($"Credential {row.Id} has no environment."),
                row.KeyType == BinanceKeyType.Ed25519 ? ExchangeKeyType.Ed25519 : ExchangeKeyType.Hmac,
                apiKey,
                secret);
        }
        catch
        {
            apiKey.Dispose();
            throw;
        }
    }

    private static IQueryable<Credential> AiRows(EcnanibDbContext context) =>
        context.Set<Credential>().AsNoTracking().Where(row => row.Kind == CredentialKind.Ai);

    /// <summary>IDs only, oldest first, so that callers go through them in a fixed order.</summary>
    private static IQueryable<AiCredential> AiCredentials(IQueryable<Credential> rows) =>
        rows
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .Select(row => new AiCredential(row.Id, row.UserId, row.Provider, row.Status));

    private async Task<AiKey> OpenAiKeyAsync(Guid userId, Guid credentialId, bool use, CancellationToken cancellationToken)
    {
        var row = await UsableAsync(userId, credentialId, CredentialKind.Ai, use, cancellationToken);
        var apiKey = await secrets.RevealAsync(row.ApiKeyId, cancellationToken);

        return new AiKey(row.Provider, row.Id, apiKey);
    }

    /// <summary>The credential, once it's known to be usable, after stamping its last use when <paramref name="use"/> is true.</summary>
    private async Task<Credential> UsableAsync(Guid userId, Guid credentialId, CredentialKind kind, bool use, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var row = await context.Set<Credential>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == credentialId && row.UserId == userId && row.Kind == kind, cancellationToken)
            ?? throw new CredentialUnusableException(credentialId, CredentialUnusableReason.NotFound);

        if (row.Status != CredentialStatus.Valid)
        {
            throw new CredentialUnusableException(credentialId, CredentialUnusableReason.NotValid);
        }

        if (!use)
        {
            return row;
        }

        var now = time.GetUtcNow();
        var stale = now - LastUseResolution;

        await context.Set<Credential>()
            .Where(current => current.Id == row.Id && (current.LastUsedAt == null || current.LastUsedAt <= stale))
            .ExecuteUpdateAsync(setters => setters.SetProperty(current => current.LastUsedAt, now), cancellationToken);

        return row;
    }
}
