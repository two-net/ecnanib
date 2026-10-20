using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecnanib.ModelSync.Services;

/// <summary>
/// One catalog sync of one provider with a stored key (FR-SYNC-02): the scheduled and the on-demand sync share it.
/// Picks Valid keys, the requester's own first, then the most recently validated, and tries at most
/// <see cref="MaxKeysPerSync"/>; a rejected key is skipped, never marked (FR-KEY-07 belongs to the keys module). The
/// AI module retries and bounds every listing (FR-AI-13). A provider-side failure becomes a Failed sync state and a
/// Failed outcome; a database failure propagates. One provider never syncs twice at once.
/// </summary>
internal sealed class ModelSyncRunner : IModelSyncService
{
    /// <summary>How many stored keys one sync may try before giving up on the provider.</summary>
    internal const int MaxKeysPerSync = 3;

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IAiClientFactory _ai;
    private readonly IProviderCatalogSync _catalog;
    private readonly ILogger<ModelSyncRunner> _logger;
    private readonly IReadOnlyDictionary<AiProvider, SemaphoreSlim> _gates =
        Enum.GetValues<AiProvider>().ToDictionary(provider => provider, _ => new SemaphoreSlim(1, 1));

    public ModelSyncRunner(IDbContextFactory<EcnanibDbContext> factory, IAiClientFactory ai, IProviderCatalogSync catalog, ILogger<ModelSyncRunner> logger)
    {
        _factory = factory;
        _ai = ai;
        _catalog = catalog;
        _logger = logger;
    }

    public Task<ModelSyncOutcome> SyncNowAsync(AiProvider provider, Guid requestedBy, CancellationToken cancellationToken) =>
        SyncAsync(provider, SyncTrigger.Manual, requestedBy, cancellationToken);

    /// <summary>The scheduler's entry (trigger Scheduled, no requester). KeyValidation is the keys module's path and is refused.</summary>
    internal async Task<ModelSyncOutcome> SyncAsync(AiProvider provider, SyncTrigger trigger, Guid? requestedBy, CancellationToken cancellationToken)
    {
        if (!_gates.TryGetValue(provider, out var gate))
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.");
        if (trigger == SyncTrigger.KeyValidation)
            throw new ArgumentException("Key-validation syncs go through IProviderCatalogSync.", nameof(trigger));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunAsync(provider, trigger, requestedBy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ModelSyncOutcome> RunAsync(AiProvider provider, SyncTrigger trigger, Guid? requestedBy, CancellationToken cancellationToken)
    {
        var candidates = await CandidateKeysAsync(provider, requestedBy, cancellationToken).ConfigureAwait(false);

        var tried = 0;
        string? lastRejection = null;
        foreach (var (keyId, apiKey) in candidates)
        {
            AiCredentials credentials;
            try
            {
                credentials = new AiCredentials(apiKey);
            }
            catch (ArgumentException)
            {
                _logger.LogInformation("Catalog sync for {Provider} ({Trigger}) skipped key {KeyId}: the stored value is malformed", provider, trigger, keyId);
                continue;
            }

            tried++;
            var handle = new AiKeyHandle(keyId.ToString("D"), provider, credentials);
            IReadOnlyList<AiModelInfo> models;
            try
            {
                models = await _ai.GetClient(provider).ListModelsAsync(handle, cancellationToken).ConfigureAwait(false);
            }
            catch (AiAuthException ex)
            {
                // The AI module scrubs the key from its messages; marking the key invalid is the re-validator's job.
                lastRejection = ex.Message;
                _logger.LogInformation("Catalog sync for {Provider} ({Trigger}): key {KeyId} was rejected: {Reason}", provider, trigger, keyId, ex.Message);
                continue;
            }
            catch (AiException ex)
            {
                return await FailAsync(provider, trigger, ex.Message, cancellationToken).ConfigureAwait(false);
            }

            // An empty answer is not a catalog: one restricted key must not count every model missing (FR-SYNC-03).
            if (models.Count == 0)
                return await FailAsync(provider, trigger, $"{provider} listed no models; the catalog was left unchanged.", cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Catalog sync for {Provider} ({Trigger}) listed {Models} model(s) with key {KeyId}", provider, trigger, models.Count, keyId);
            var result = await _catalog.SyncAsync(provider, models, trigger, cancellationToken).ConfigureAwait(false);
            return new ModelSyncOutcome(ModelSyncStatus.Synced, provider, result);
        }

        if (tried == 0)
        {
            _logger.LogDebug("Catalog sync for {Provider} ({Trigger}) skipped: no valid key is stored", provider, trigger);
            return new ModelSyncOutcome(ModelSyncStatus.NoValidKey, provider, Detail: $"No valid {provider} key is stored.");
        }

        return await FailAsync(provider, trigger, $"{provider} rejected every key tried ({tried}); last answer: {lastRejection}", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Valid keys of the provider, the requester's own first, then the most recently validated; at most <see cref="MaxKeysPerSync"/> leave the database.</summary>
    private async Task<List<(Guid Id, string ApiKey)>> CandidateKeysAsync(AiProvider provider, Guid? requestedBy, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var valid = db.ProviderKeys.AsNoTracking().Where(k => k.Provider == provider && k.Status == KeyStatus.Valid);
        var ordered = requestedBy is { } user
            ? valid.OrderBy(k => k.UserId == user ? 0 : 1).ThenByDescending(k => k.LastValidatedAt ?? DateTimeOffset.MinValue).ThenBy(k => k.Id)
            : valid.OrderByDescending(k => k.LastValidatedAt ?? DateTimeOffset.MinValue).ThenBy(k => k.Id);
        var rows = await ordered.Take(MaxKeysPerSync)
            .Select(k => new { k.Id, k.ApiKey })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(k => (k.Id, k.ApiKey)).ToList();
    }

    private async Task<ModelSyncOutcome> FailAsync(AiProvider provider, SyncTrigger trigger, string error, CancellationToken cancellationToken)
    {
        _logger.Log(trigger == SyncTrigger.Scheduled ? LogLevel.Warning : LogLevel.Information,
            "Catalog sync for {Provider} ({Trigger}) failed: {Reason}", provider, trigger, error);
        await _catalog.RecordFailureAsync(provider, trigger, error, cancellationToken).ConfigureAwait(false);
        return new ModelSyncOutcome(ModelSyncStatus.Failed, provider, Detail: error);
    }
}
