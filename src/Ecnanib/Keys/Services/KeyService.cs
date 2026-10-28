using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Keys.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ecnanib.Keys.Services;

/// <summary>
/// The user's keys over the database (FR-KEY-01..09). Every key is proven with its own service before it is stored
/// and only a Valid key is ever written, encrypted under the row's own id through the database module's protector
/// (FR-KEY-04). Logs carry the user id, key id, environment or provider and the outcome: never a key value and never a
/// label, which is the user's own text (FR-LOG-04, FR-LOG-05).
/// </summary>
internal sealed class KeyService : IKeyService
{
    internal const int MaxLabelLength = 100;

    /// <summary>How often a deletion repeats the clear-then-delete when an agent takes the key in between.</summary>
    internal const int DeleteAttempts = 3;

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IStoredKeyProtector _protector;
    private readonly IExchangeClientFactory _exchange;
    private readonly ExchangeKeyValidator _exchangeValidator;
    private readonly ProviderKeyValidator _providerValidator;
    private readonly StoredKeyRevalidation _revalidation;
    private readonly IKeyDependentAgents _agents;
    private readonly TimeProvider _time;
    private readonly ILogger<KeyService> _logger;

    public KeyService(
        IDbContextFactory<EcnanibDbContext> factory,
        IStoredKeyProtector protector,
        IExchangeClientFactory exchange,
        ExchangeKeyValidator exchangeValidator,
        ProviderKeyValidator providerValidator,
        StoredKeyRevalidation revalidation,
        IKeyDependentAgents agents,
        TimeProvider time,
        ILogger<KeyService> logger)
    {
        _factory = factory;
        _protector = protector;
        _exchange = exchange;
        _exchangeValidator = exchangeValidator;
        _providerValidator = providerValidator;
        _revalidation = revalidation;
        _agents = agents;
        _time = time;
        _logger = logger;
    }

    public async Task<KeyOverview> GetOverviewAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var exchangeKeys = await db.ExchangeKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .OrderBy(k => k.CreatedAt).ThenBy(k => k.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var activeByKey = await db.Agents.AsNoTracking()
            .Where(a => a.UserId == userId && a.ExchangeKeyId != null && a.Mode == AgentMode.Live && (a.State == AgentState.Running || a.State == AgentState.Paused))
            .GroupBy(a => a.ExchangeKeyId!.Value)
            .Select(g => new { KeyId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.KeyId, x => x.Count, cancellationToken)
            .ConfigureAwait(false);
        var providerKeys = await db.ProviderKeys.AsNoTracking()
            .Where(k => k.UserId == userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var activeByProvider = await ActiveAgentsByProviderAsync(db, userId, cancellationToken).ConfigureAwait(false);

        var slots = Enum.GetValues<AiProvider>()
            .Select(p => new ProviderKeySlot(p, providerKeys.SingleOrDefault(k => k.Provider == p) is { } key ? ToView(key) : null, activeByProvider.GetValueOrDefault(p)))
            .ToList();
        return new KeyOverview(exchangeKeys.Select(k => ToView(k, activeByKey.GetValueOrDefault(k.Id))).ToList(), slots);
    }

    public async Task<ExchangeKeyView?> GetExchangeKeyAsync(Guid userId, Guid keyId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ExchangeKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == keyId && k.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (key is null)
            return null;
        var active = await AgentKeyTransitions.ActiveOnExchangeKey(db, keyId).CountAsync(cancellationToken).ConfigureAwait(false);
        return ToView(key, active);
    }

    public async Task<ProviderKeySlot> GetProviderSlotAsync(Guid userId, AiProvider provider, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ProviderKeys.AsNoTracking().SingleOrDefaultAsync(k => k.UserId == userId && k.Provider == provider, cancellationToken).ConfigureAwait(false);
        var active = await AgentKeyTransitions.ActiveOnProvider(db, userId, provider).CountAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderKeySlot(provider, key is null ? null : ToView(key), active);
    }

    public async Task<IReadOnlySet<AiProvider>> ValidProvidersAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var providers = await db.ProviderKeys.AsNoTracking()
            .Where(k => k.UserId == userId && k.Status == KeyStatus.Valid)
            .Select(k => k.Provider)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return providers.ToHashSet();
    }

    public async Task<KeySaveResult> AddExchangeKeyAsync(Guid userId, ExchangeKeyInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var label = input.Label?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > MaxLabelLength)
            return new KeySaveResult(KeySaveStatus.InvalidInput, Detail: KeyReasons.InvalidLabel);
        if (!input.AllowSpot && !input.AllowFutures)
            return new KeySaveResult(KeySaveStatus.NoMarketSelected);
        if (!TryCredentials(input.ApiKey, input.ApiSecret, out var credentials))
            return new KeySaveResult(KeySaveStatus.InvalidInput, Detail: KeyReasons.InvalidExchangeCredentials);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Cheap check first so a duplicate label does not cost a round trip to the exchange; the unique index settles races.
        if (await db.ExchangeKeys.AnyAsync(k => k.UserId == userId && k.Label == label, cancellationToken).ConfigureAwait(false))
            return new KeySaveResult(KeySaveStatus.LabelTaken);

        var validation = await _exchangeValidator.ValidateAsync(input.Environment, credentials, input.AllowSpot, input.AllowFutures, cancellationToken).ConfigureAwait(false);
        if (validation.Verdict != ValidationVerdict.Valid)
            return Refused(userId, "Exchange", validation.Verdict, validation.WithdrawalEnabled, validation.Reason);

        var now = _time.GetUtcNow();
        // The ciphertext is bound to the row id, so the id exists before the values are protected.
        var id = Guid.CreateVersion7();
        var key = new ExchangeKey
        {
            Id = id,
            UserId = userId,
            Label = label,
            Environment = input.Environment,
            AllowSpot = input.AllowSpot,
            AllowFutures = input.AllowFutures,
            ProtectedApiKey = _protector.Protect(StoredKeyColumn.ExchangeApiKey, id, credentials.ApiKey),
            ProtectedApiSecret = _protector.Protect(StoredKeyColumn.ExchangeApiSecret, id, credentials.ApiSecret),
            Cipher = StoredKeyCipher.AesGcm,
            PermissionsSnapshot = PermissionsSnapshot.Serialize(validation.Permissions!),
            Status = KeyStatus.Valid,
            LastValidatedAt = now,
            LastValidationError = null,
            CreatedAt = now,
        };
        db.ExchangeKeys.Add(key);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a race on (user, label). The exception text names the label, so it is not logged.
            return new KeySaveResult(KeySaveStatus.LabelTaken);
        }

        _logger.LogInformation("User {UserId} added exchange key {KeyId} on {Environment} (spot {Spot}, futures {Futures}); permissions verified: {Verified}",
            userId, key.Id, key.Environment, key.AllowSpot, key.AllowFutures, validation.Permissions!.Verified);
        return new KeySaveResult(KeySaveStatus.Succeeded, key.Id);
    }

    public async Task<KeySaveResult> ReplaceExchangeKeyAsync(Guid userId, Guid keyId, string? apiKey, string? apiSecret, CancellationToken cancellationToken)
    {
        if (!TryCredentials(apiKey, apiSecret, out var credentials))
            return new KeySaveResult(KeySaveStatus.InvalidInput, Detail: KeyReasons.InvalidExchangeCredentials);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ExchangeKeys.SingleOrDefaultAsync(k => k.Id == keyId && k.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (key is null)
            return new KeySaveResult(KeySaveStatus.NotFound);

        var validation = await _exchangeValidator.ValidateAsync(key.Environment, credentials, key.AllowSpot, key.AllowFutures, cancellationToken).ConfigureAwait(false);
        if (validation.Verdict != ValidationVerdict.Valid)
            return Refused(userId, "Exchange", validation.Verdict, validation.WithdrawalEnabled, validation.Reason);

        _protector.Protect(key, credentials.ApiKey, credentials.ApiSecret);
        key.PermissionsSnapshot = PermissionsSnapshot.Serialize(validation.Permissions!);
        key.Status = KeyStatus.Valid;
        key.LastValidatedAt = _time.GetUtcNow();
        key.LastValidationError = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Clients cached under the stored id were built on the old secret; the factory refuses the new one until released.
        await ReleaseQuietlyAsync(keyId).ConfigureAwait(false);
        _logger.LogInformation("User {UserId} replaced exchange key {KeyId}; permissions verified: {Verified}", userId, keyId, validation.Permissions!.Verified);
        return new KeySaveResult(KeySaveStatus.Succeeded, keyId);
    }

    public async Task<KeyValidationResult> ValidateExchangeKeyAsync(Guid userId, Guid keyId, CancellationToken cancellationToken)
    {
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await db.ExchangeKeys.AnyAsync(k => k.Id == keyId && k.UserId == userId, cancellationToken).ConfigureAwait(false))
                return new KeyValidationResult(KeyValidationOutcome.NotFound);
        }

        return await _revalidation.RevalidateExchangeKeyAsync(keyId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KeyDeletionResult> DeleteExchangeKeyAsync(Guid userId, Guid keyId, bool stopAgents, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.ExchangeKeys.AnyAsync(k => k.Id == keyId && k.UserId == userId, cancellationToken).ConfigureAwait(false))
            return new KeyDeletionResult(KeyDeletionStatus.NotFound);

        var active = await AgentKeyTransitions.ActiveOnExchangeKey(db, keyId).Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (active.Count > 0 && !stopAgents)
        {
            _logger.LogInformation("Deletion of exchange key {KeyId} by user {UserId} refused: {Agents} agent(s) running or paused", keyId, userId, active.Count);
            return new KeyDeletionResult(KeyDeletionStatus.AgentsInUse, active.Count);
        }

        var stopped = active.Count == 0 ? 0 : await _agents.StopAsync(active, KeyReasons.StoppedExchangeKeyDeleted, cancellationToken).ConfigureAwait(false);
        // FR-KEY-06: every agent that referenced the key loses the reference, then the key goes, in one transaction; the
        // database's (exchange_key_id, user_id) constraint keeps agents on their owner's keys and no longer clears the
        // column itself (NFR-SEC-03). Database:RetryOnFailureCount enables the retrying strategy, so the transaction runs inside it.
        var strategy = db.Database.CreateExecutionStrategy();
        int deleted;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                deleted = await strategy.ExecuteAsync(async token =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
                    await db.Agents.Where(a => a.ExchangeKeyId == keyId && a.UserId == userId)
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExchangeKeyId, (Guid?)null), token)
                        .ConfigureAwait(false);
                    var rows = await db.ExchangeKeys.Where(k => k.Id == keyId && k.UserId == userId).ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return rows;
                }, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (DbUpdateExceptions.IsForeignKeyViolation(ex) && attempt < DeleteAttempts)
            {
                // An agent of the user took the key between the clearing and the delete; the constraint that used to SET NULL
                // refuses the delete instead (NFR-SEC-03), so the next attempt clears that agent too.
                _logger.LogDebug("Exchange key {KeyId} was referenced again while being deleted; clearing again (attempt {Attempt})", keyId, attempt);
            }
        }

        if (deleted == 0)
            return new KeyDeletionResult(KeyDeletionStatus.NotFound);

        await ReleaseQuietlyAsync(keyId).ConfigureAwait(false);
        _logger.LogInformation("User {UserId} removed exchange key {KeyId}; {Agents} agent(s) stopped", userId, keyId, stopped);
        return new KeyDeletionResult(KeyDeletionStatus.Succeeded, stopped);
    }

    public async Task<KeySaveResult> SetProviderKeyAsync(Guid userId, AiProvider provider, string? apiKey, CancellationToken cancellationToken)
    {
        AiCredentials credentials;
        try
        {
            credentials = new AiCredentials(apiKey ?? string.Empty);
        }
        catch (ArgumentException)
        {
            return new KeySaveResult(KeySaveStatus.InvalidInput, Detail: KeyReasons.InvalidProviderKey);
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.ProviderKeys.SingleOrDefaultAsync(k => k.UserId == userId && k.Provider == provider, cancellationToken).ConfigureAwait(false);

        var validation = await _providerValidator.ValidateAsync(provider, credentials, cancellationToken).ConfigureAwait(false);
        if (validation.Verdict != ValidationVerdict.Valid)
            return Refused(userId, ProviderNames.Display(provider), validation.Verdict, false, validation.Reason);

        var now = _time.GetUtcNow();
        var replaced = key is not null;
        if (key is null)
        {
            key = new ProviderKey { Id = Guid.CreateVersion7(), UserId = userId, Provider = provider, ProtectedApiKey = [], CreatedAt = now };
            db.ProviderKeys.Add(key);
        }

        Apply(key, credentials, now);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a race adding the first key for this provider; the row exists now, so it is replaced instead.
            db.ChangeTracker.Clear();
            key = await db.ProviderKeys.SingleAsync(k => k.UserId == userId && k.Provider == provider, cancellationToken).ConfigureAwait(false);
            Apply(key, credentials, now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            replaced = true;
        }

        var models = validation.Models!;
        _logger.LogInformation("User {UserId} {Action} {Provider} key {KeyId}; {Models} model(s) listed", userId, replaced ? "replaced" : "added", provider, key.Id, models.Count);
        var synced = await _revalidation.SyncCatalogAsync(provider, models, cancellationToken).ConfigureAwait(false);
        return new KeySaveResult(KeySaveStatus.Succeeded, key.Id, ModelCount: models.Count, CatalogSynced: synced);

        // Protects under the id of the row it is applied to: after a lost race that is the row that won.
        void Apply(ProviderKey key, AiCredentials credentials, DateTimeOffset now)
        {
            _protector.Protect(key, credentials.ApiKey);
            key.Status = KeyStatus.Valid;
            key.LastValidatedAt = now;
            key.LastValidationError = null;
        }
    }

    public async Task<KeyValidationResult> ValidateProviderKeyAsync(Guid userId, AiProvider provider, CancellationToken cancellationToken)
    {
        Guid keyId;
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            keyId = await db.ProviderKeys.AsNoTracking()
                .Where(k => k.UserId == userId && k.Provider == provider)
                .Select(k => k.Id)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (keyId == Guid.Empty)
                return new KeyValidationResult(KeyValidationOutcome.NotFound);
        }

        return await _revalidation.RevalidateProviderKeyAsync(keyId, syncCatalog: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<KeyDeletionResult> DeleteProviderKeyAsync(Guid userId, AiProvider provider, bool stopAgents, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (!await db.ProviderKeys.AnyAsync(k => k.UserId == userId && k.Provider == provider, cancellationToken).ConfigureAwait(false))
            return new KeyDeletionResult(KeyDeletionStatus.NotFound);

        var active = await AgentKeyTransitions.ActiveOnProvider(db, userId, provider).Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (active.Count > 0 && !stopAgents)
        {
            _logger.LogInformation("Deletion of the {Provider} key by user {UserId} refused: {Agents} agent(s) running or paused", provider, userId, active.Count);
            return new KeyDeletionResult(KeyDeletionStatus.AgentsInUse, active.Count);
        }

        var stopped = active.Count == 0 ? 0 : await _agents.StopAsync(active, KeyReasons.StoppedProviderKeyDeleted(provider), cancellationToken).ConfigureAwait(false);
        var deleted = await db.ProviderKeys.Where(k => k.UserId == userId && k.Provider == provider).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (deleted == 0)
            return new KeyDeletionResult(KeyDeletionStatus.NotFound);

        _logger.LogInformation("User {UserId} removed their {Provider} key; {Agents} agent(s) stopped", userId, provider, stopped);
        return new KeyDeletionResult(KeyDeletionStatus.Succeeded, stopped);
    }

    private KeySaveResult Refused(Guid userId, string service, ValidationVerdict verdict, bool withdrawalEnabled, string? reason)
    {
        var status = verdict switch
        {
            ValidationVerdict.Invalid when withdrawalEnabled => KeySaveStatus.WithdrawalEnabled,
            ValidationVerdict.Invalid => KeySaveStatus.Rejected,
            _ => KeySaveStatus.Failed,
        };
        _logger.LogInformation("{Service} key for user {UserId} not stored: {Status} ({Reason})", service, userId, status, reason);
        return new KeySaveResult(status, Detail: reason);
    }

    private async Task ReleaseQuietlyAsync(Guid keyId)
    {
        try
        {
            await _exchange.ReleaseAsync(keyId.ToString("D"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not release the exchange clients of key {KeyId}", keyId);
        }
    }

    private static async Task<Dictionary<AiProvider, int>> ActiveAgentsByProviderAsync(EcnanibDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var pairs = await db.CouncilAssignments.AsNoTracking()
            .Where(c => c.Enabled && c.Agent.UserId == userId && (c.Agent.State == AgentState.Running || c.Agent.State == AgentState.Paused))
            .Select(c => new { c.Provider, c.AgentId })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return pairs.GroupBy(p => p.Provider).ToDictionary(g => g.Key, g => g.Count());
    }

    private static bool TryCredentials(string? apiKey, string? apiSecret, out ExchangeCredentials credentials)
    {
        try
        {
            credentials = new ExchangeCredentials(apiKey ?? string.Empty, apiSecret ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            credentials = null!;
            return false;
        }
    }

    private ExchangeKeyView ToView(ExchangeKey key, int activeAgents) => new(
        key.Id, key.Label, key.Environment, key.AllowSpot, key.AllowFutures, ExchangeCredentials.Mask(MaskSource(key)), key.Status,
        key.LastValidatedAt, key.LastValidationError, PermissionsSnapshot.Deserialize(key.PermissionsSnapshot), activeAgents, key.CreatedAt);

    private ProviderKeyView ToView(ProviderKey key) => new(
        key.Id, key.Provider, AiCredentials.Mask(MaskSource(key)), key.Status, key.LastValidatedAt, key.LastValidationError, key.CreatedAt);

    /// <summary>The value the view's mask is cut from (FR-KEY-04); a stored value that does not open masks to "…" and is logged by id.</summary>
    private string MaskSource(ExchangeKey key)
    {
        try
        {
            return _protector.Unprotect(key).ApiKey;
        }
        catch (StoredKeyProtectionException ex)
        {
            _logger.LogWarning(ex, "Exchange key {KeyId} of user {UserId} does not open; shown without its mask", key.Id, key.UserId);
            return string.Empty;
        }
    }

    private string MaskSource(ProviderKey key)
    {
        try
        {
            return _protector.Unprotect(key);
        }
        catch (StoredKeyProtectionException ex)
        {
            _logger.LogWarning(ex, "{Provider} key {KeyId} of user {UserId} does not open; shown without its mask", key.Provider, key.Id, key.UserId);
            return string.Empty;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
