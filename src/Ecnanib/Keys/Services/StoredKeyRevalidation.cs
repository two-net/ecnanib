using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Ecnanib.ModelSync.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Keys.Services;

/// <summary>
/// Re-validates one stored key (FR-KEY-07), shared by "validate now" and the scheduled job. Outcomes are written with
/// an update guarded by the key value that was checked: when a replacement landed meanwhile the update matches nothing
/// and the agents are left alone. An invalid key moves the Running and Paused agents using it to Error with the
/// cause; a key that recovers goes back to Valid while those agents stay in Error until the user starts them.
/// </summary>
internal sealed class StoredKeyRevalidation
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly ExchangeKeyValidator _exchangeValidator;
    private readonly ProviderKeyValidator _providerValidator;
    private readonly IKeyDependentAgents _agents;
    private readonly IProviderCatalogSync _catalog;
    private readonly TimeProvider _time;
    private readonly ILogger<StoredKeyRevalidation> _logger;

    public StoredKeyRevalidation(
        IDbContextFactory<EcnanibDbContext> factory,
        ExchangeKeyValidator exchangeValidator,
        ProviderKeyValidator providerValidator,
        IKeyDependentAgents agents,
        IProviderCatalogSync catalog,
        TimeProvider time,
        ILogger<StoredKeyRevalidation> logger)
    {
        _factory = factory;
        _exchangeValidator = exchangeValidator;
        _providerValidator = providerValidator;
        _agents = agents;
        _catalog = catalog;
        _time = time;
        _logger = logger;
    }

    public async Task<KeyValidationResult> RevalidateExchangeKeyAsync(Guid keyId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ExchangeKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == keyId, cancellationToken).ConfigureAwait(false);
        if (key is null)
            return new KeyValidationResult(KeyValidationOutcome.NotFound);

        ExchangeKeyValidation validation;
        ExchangeCredentials? credentials = null;
        try
        {
            credentials = new ExchangeCredentials(key.ApiKey, key.ApiSecret);
        }
        catch (ArgumentException)
        {
        }

        validation = credentials is null
            ? new ExchangeKeyValidation(ValidationVerdict.Invalid, Reason: KeyReasons.MalformedStoredKey)
            : await _exchangeValidator.ValidateAsync(key.Environment, credentials, key.AllowSpot, key.AllowFutures, cancellationToken).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var checkedKey = key.ApiKey;
        switch (validation.Verdict)
        {
            case ValidationVerdict.Valid:
            {
                var snapshot = PermissionsSnapshot.Serialize(validation.Permissions!);
                var rows = await db.ExchangeKeys
                    .Where(k => k.Id == keyId && k.ApiKey == checkedKey)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(k => k.Status, KeyStatus.Valid)
                        .SetProperty(k => k.LastValidatedAt, now)
                        .SetProperty(k => k.LastValidationError, (string?)null)
                        .SetProperty(k => k.PermissionsSnapshot, snapshot), cancellationToken)
                    .ConfigureAwait(false);
                if (rows == 1 && key.Status != KeyStatus.Valid)
                    _logger.LogInformation("Exchange key {KeyId} of user {UserId} is valid again", keyId, key.UserId);
                else
                    _logger.LogDebug("Exchange key {KeyId} of user {UserId} re-validated: valid", keyId, key.UserId);
                return new KeyValidationResult(KeyValidationOutcome.Valid);
            }

            case ValidationVerdict.Invalid:
            {
                var reason = validation.Reason ?? "The exchange refused the key.";
                var rows = await db.ExchangeKeys
                    .Where(k => k.Id == keyId && k.ApiKey == checkedKey)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(k => k.Status, KeyStatus.Invalid)
                        .SetProperty(k => k.LastValidatedAt, now)
                        .SetProperty(k => k.LastValidationError, reason), cancellationToken)
                    .ConfigureAwait(false);
                var failed = 0;
                if (rows == 1)
                {
                    var ids = await AgentKeyTransitions.ActiveOnExchangeKey(db, keyId).Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                    failed = await _agents.FailAsync(ids, KeyReasons.ExchangeKeyInvalid(key.Label, reason), cancellationToken).ConfigureAwait(false);
                }

                _logger.LogWarning("Exchange key {KeyId} of user {UserId} is invalid: {Reason}; {Agents} agent(s) entered Error", keyId, key.UserId, reason, failed);
                return new KeyValidationResult(KeyValidationOutcome.Invalid, reason, failed);
            }

            default:
                _logger.LogInformation("Exchange key {KeyId} of user {UserId} could not be checked: {Reason}", keyId, key.UserId, validation.Reason);
                return new KeyValidationResult(KeyValidationOutcome.Failed, validation.Reason);
        }
    }

    public async Task<KeyValidationResult> RevalidateProviderKeyAsync(Guid keyId, bool syncCatalog, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ProviderKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == keyId, cancellationToken).ConfigureAwait(false);
        if (key is null)
            return new KeyValidationResult(KeyValidationOutcome.NotFound);

        AiCredentials? credentials = null;
        try
        {
            credentials = new AiCredentials(key.ApiKey);
        }
        catch (ArgumentException)
        {
        }

        var validation = credentials is null
            ? new ProviderKeyValidation(ValidationVerdict.Invalid, Reason: KeyReasons.MalformedStoredKey)
            : await _providerValidator.ValidateAsync(key.Provider, credentials, cancellationToken).ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var checkedKey = key.ApiKey;
        switch (validation.Verdict)
        {
            case ValidationVerdict.Valid:
            {
                var rows = await db.ProviderKeys
                    .Where(k => k.Id == keyId && k.ApiKey == checkedKey)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(k => k.Status, KeyStatus.Valid)
                        .SetProperty(k => k.LastValidatedAt, now)
                        .SetProperty(k => k.LastValidationError, (string?)null), cancellationToken)
                    .ConfigureAwait(false);
                if (rows == 1 && key.Status != KeyStatus.Valid)
                    _logger.LogInformation("{Provider} key {KeyId} of user {UserId} is valid again", key.Provider, keyId, key.UserId);
                else
                    _logger.LogDebug("{Provider} key {KeyId} of user {UserId} re-validated: valid", key.Provider, keyId, key.UserId);
                var models = validation.Models!;
                var synced = syncCatalog && await SyncCatalogAsync(key.Provider, models, cancellationToken).ConfigureAwait(false);
                return new KeyValidationResult(KeyValidationOutcome.Valid, ModelCount: models.Count, CatalogSynced: synced);
            }

            case ValidationVerdict.Invalid:
            {
                var reason = validation.Reason ?? "The provider refused the key.";
                var rows = await db.ProviderKeys
                    .Where(k => k.Id == keyId && k.ApiKey == checkedKey)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(k => k.Status, KeyStatus.Invalid)
                        .SetProperty(k => k.LastValidatedAt, now)
                        .SetProperty(k => k.LastValidationError, reason), cancellationToken)
                    .ConfigureAwait(false);
                var failed = 0;
                if (rows == 1)
                {
                    var ids = await AgentKeyTransitions.ActiveOnProvider(db, key.UserId, key.Provider).Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                    failed = await _agents.FailAsync(ids, KeyReasons.ProviderKeyInvalid(key.Provider, reason), cancellationToken).ConfigureAwait(false);
                }

                _logger.LogWarning("{Provider} key {KeyId} of user {UserId} is invalid: {Reason}; {Agents} agent(s) entered Error", key.Provider, keyId, key.UserId, reason, failed);
                return new KeyValidationResult(KeyValidationOutcome.Invalid, reason, failed);
            }

            default:
                _logger.LogInformation("{Provider} key {KeyId} of user {UserId} could not be checked: {Reason}", key.Provider, keyId, key.UserId, validation.Reason);
                return new KeyValidationResult(KeyValidationOutcome.Failed, validation.Reason);
        }
    }

    /// <summary>Feeds the catalog after a user-triggered validation (FR-SYNC-02). A catalog failure is logged and never fails the key operation.</summary>
    public async Task<bool> SyncCatalogAsync(AiProvider provider, IReadOnlyList<AiModelInfo> models, CancellationToken cancellationToken)
    {
        try
        {
            await _catalog.SyncAsync(provider, models, SyncTrigger.KeyValidation, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Catalog sync for {Provider} after key validation failed; the key is stored", provider);
            return false;
        }
    }
}
