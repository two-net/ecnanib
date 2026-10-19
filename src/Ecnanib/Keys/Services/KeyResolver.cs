using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Keys.Services;

/// <summary>Reads a stored key into a handle for the exchange or AI module; stored keys use their entity id as the handle id.</summary>
internal sealed class KeyResolver : IKeyResolver
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public KeyResolver(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<KeyResolution<ExchangeKeyHandle>> ResolveExchangeKeyAsync(Guid userId, Guid keyId, MarketType market, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ExchangeKeys.AsNoTracking()
            .Where(k => k.Id == keyId && k.UserId == userId)
            .Select(k => new { k.Label, k.Environment, k.AllowSpot, k.AllowFutures, k.ApiKey, k.ApiSecret, k.Status, k.LastValidationError })
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
            return new KeyResolution<ExchangeKeyHandle>(KeyResolutionStatus.MarketNotAllowed, Reason: $"Key '{key.Label}' is not enabled for {market}.");

        ExchangeCredentials credentials;
        try
        {
            credentials = new ExchangeCredentials(key.ApiKey, key.ApiSecret);
        }
        catch (ArgumentException)
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
            .Select(k => new { k.Id, k.ApiKey, k.Status, k.LastValidationError })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (key is null)
            return new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Missing, Reason: $"No {ProviderNames.Display(provider)} key.");
        if (key.Status != KeyStatus.Valid)
            return new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Invalid, Reason: key.LastValidationError ?? "The key has not been validated.");

        AiCredentials credentials;
        try
        {
            credentials = new AiCredentials(key.ApiKey);
        }
        catch (ArgumentException)
        {
            return new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Invalid, Reason: KeyReasons.MalformedStoredKey);
        }

        return new KeyResolution<AiKeyHandle>(KeyResolutionStatus.Resolved, new AiKeyHandle(key.Id.ToString("D"), provider, credentials));
    }
}
