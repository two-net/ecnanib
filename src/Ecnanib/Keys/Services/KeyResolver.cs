using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Keys.Services;

/// <summary>
/// Reads a stored key into a handle for the exchange or AI module; stored keys use their entity id as the handle id.
/// The values are decrypted through the database module's protector (FR-KEY-04); a stored value that does not open
/// resolves as Invalid with the malformed-key reason, like a value its own constructor refuses.
/// </summary>
internal sealed class KeyResolver : IKeyResolver
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IStoredKeyProtector _protector;

    public KeyResolver(IDbContextFactory<EcnanibDbContext> factory, IStoredKeyProtector protector)
    {
        _factory = factory;
        _protector = protector;
    }

    public async Task<KeyResolution<ExchangeKeyHandle>> ResolveExchangeKeyAsync(Guid userId, Guid keyId, MarketType market, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ExchangeKeys.AsNoTracking()
            .Where(k => k.Id == keyId && k.UserId == userId)
            .Select(k => new { k.Label, k.Environment, k.AllowSpot, k.AllowFutures, k.ProtectedApiKey, k.ProtectedApiSecret, k.Cipher, k.Status, k.LastValidationError })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (key is null)
            return new KeyResolution<ExchangeKeyHandle>(KeyResolutionStatus.Missing, Reason: "No such exchange key.");
        if (key.Status != KeyStatus.Valid)
            return new KeyResolution<ExchangeKeyHandle>(KeyResolutionStatus.Invalid, Reason: key.LastValidationError ?? "The key has not been validated.");

        var allowed = market switch
        {
            MarketType.Spot => key.AllowSpot,
            MarketType.UsdFutures => key.AllowFutures,
            _ => false,
        };
        if (!allowed)
            return new KeyResolution<ExchangeKeyHandle>(KeyResolutionStatus.MarketNotAllowed, Reason: $"The exchange key is not enabled for {market}.");

        ExchangeCredentials credentials;
        try
        {
            credentials = new ExchangeCredentials(
                _protector.Unprotect(StoredKeyColumn.ExchangeApiKey, keyId, key.Cipher, key.ProtectedApiKey),
                _protector.Unprotect(StoredKeyColumn.ExchangeApiSecret, keyId, key.Cipher, key.ProtectedApiSecret));
        }
        catch (Exception ex) when (ex is ArgumentException or StoredKeyProtectionException)
        {
            return new KeyResolution<ExchangeKeyHandle>(KeyResolutionStatus.Invalid, Reason: KeyReasons.MalformedStoredKey);
        }

        return new KeyResolution<ExchangeKeyHandle>(KeyResolutionStatus.Resolved, new ExchangeKeyHandle(keyId.ToString("D"), key.Environment, credentials));
    }

    public async Task<KeyResolution<AiKeyHandle>> ResolveProviderKeyAsync(Guid userId, AiProvider provider, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ProviderKeys.AsNoTracking()
            .Where(k => k.UserId == userId && k.Provider == provider)
            .Select(k => new { k.Id, k.ProtectedApiKey, k.Cipher, k.Status, k.LastValidationError })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (key is null)
            return new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Missing, Reason: $"No {ProviderNames.Display(provider)} key.");
        if (key.Status != KeyStatus.Valid)
            return new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Invalid, Reason: key.LastValidationError ?? "The key has not been validated.");

        AiCredentials credentials;
        try
        {
            credentials = new AiCredentials(_protector.Unprotect(StoredKeyColumn.ProviderApiKey, key.Id, key.Cipher, key.ProtectedApiKey));
        }
        catch (Exception ex) when (ex is ArgumentException or StoredKeyProtectionException)
        {
            return new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Invalid, Reason: KeyReasons.MalformedStoredKey);
        }

        return new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Resolved, new AiKeyHandle(key.Id.ToString("D"), provider, credentials));
    }
}
