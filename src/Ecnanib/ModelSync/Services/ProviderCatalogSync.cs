using Ecnanib.Ai.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.ModelSync.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ecnanib.ModelSync.Services;

/// <summary>
/// Catalog upsert from a model list that was already fetched (FR-SYNC-01, FR-SYNC-06). Models missing from the list
/// are left untouched: counting misses and deprecating belongs to the scheduled sync (FR-SYNC-03). The catalog is
/// shared by all users; two users validating the same provider at once race on the unique index and the loser retries.
/// </summary>
internal sealed class ProviderCatalogSync : IProviderCatalogSync
{
    /// <summary>Column limit of <c>provider_models.model_id</c> and <c>display_name</c>.</summary>
    internal const int MaxLength = 200;

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly TimeProvider _time;
    private readonly ILogger<ProviderCatalogSync> _logger;

    public ProviderCatalogSync(IDbContextFactory<EcnanibDbContext> factory, TimeProvider time, ILogger<ProviderCatalogSync> logger)
    {
        _factory = factory;
        _time = time;
        _logger = logger;
    }

    public async Task<CatalogSyncResult> SyncAsync(AiProvider provider, IReadOnlyList<AiModelInfo> models, SyncTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (models.Any(m => m.Provider != provider))
            throw new ArgumentException($"Every model must belong to {provider}.", nameof(models));

        var listed = new List<AiModelInfo>(models.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
                continue;
            if (model.Id.Length > MaxLength)
            {
                _logger.LogWarning("Catalog sync for {Provider} skipped a model whose id is {Length} characters long (limit {Limit})", provider, model.Id.Length, MaxLength);
                continue;
            }

            if (seen.Add(model.Id))
                listed.Add(model);
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        CatalogSyncResult result;
        try
        {
            try
            {
                result = await RunAsync(db, provider, listed, trigger, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another sync of the same provider inserted a row between our read and our write; read again and update it.
                _logger.LogDebug("Catalog sync for {Provider} lost a race on the unique index; retrying once", provider);
                result = await RunAsync(db, provider, listed, trigger, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordFailureAsync(provider, trigger, ex, cancellationToken).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation("Catalog sync for {Provider} ({Trigger}): {Added} added, {Updated} updated, {Listed} listed", provider, trigger, result.Added, result.Updated, result.Listed);
        return result;
    }

    private async Task<CatalogSyncResult> RunAsync(EcnanibDbContext db, AiProvider provider, List<AiModelInfo> listed, SyncTrigger trigger, CancellationToken cancellationToken)
    {
        // Database:RetryOnFailureCount enables the retrying strategy; a transaction of our own must run inside it.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var existing = await db.ProviderModels.Where(m => m.Provider == provider).ToDictionaryAsync(m => m.ModelId, StringComparer.Ordinal, token).ConfigureAwait(false);
            var added = 0;
            var updated = 0;
            foreach (var model in listed)
            {
                var displayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName.Trim();
                if (displayName.Length > MaxLength)
                    displayName = displayName[..MaxLength];

                if (existing.TryGetValue(model.Id, out var row))
                {
                    row.DisplayName = displayName;
                    row.ProviderCreatedAt = model.CreatedAt;
                    row.InputTokenLimit = model.InputTokenLimit;
                    row.OutputTokenLimit = model.OutputTokenLimit;
                    row.SupportsTextGeneration = model.SupportsTextGeneration;
                    row.Capabilities = CloneOrEmpty(model.Capabilities);
                    row.LastSeenAt = now;
                    row.Status = ModelStatus.Available;
                    row.MissedSyncs = 0;
                    updated++;
                }
                else
                {
                    db.ProviderModels.Add(new ProviderModel
                    {
                        Provider = provider,
                        ModelId = model.Id,
                        DisplayName = displayName,
                        ProviderCreatedAt = model.CreatedAt,
                        InputTokenLimit = model.InputTokenLimit,
                        OutputTokenLimit = model.OutputTokenLimit,
                        SupportsTextGeneration = model.SupportsTextGeneration,
                        Capabilities = CloneOrEmpty(model.Capabilities),
                        FirstSeenAt = now,
                        LastSeenAt = now,
                        Status = ModelStatus.Available,
                        MissedSyncs = 0,
                    });
                    added++;
                }
            }

            var state = await db.ProviderSyncStates.SingleOrDefaultAsync(s => s.Provider == provider, token).ConfigureAwait(false);
            if (state is null)
            {
                state = new ProviderSyncState { Provider = provider };
                db.ProviderSyncStates.Add(state);
            }

            state.LastSyncAt = now;
            state.LastStatus = SyncStatus.Succeeded;
            state.LastError = null;
            state.ModelCount = listed.Count;
            state.Trigger = trigger;

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new CatalogSyncResult(provider, added, updated, listed.Count, now);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Best effort: the failure is what FR-SYNC-06 shows as the last result; losing that record must not hide the original error.</summary>
    private async Task RecordFailureAsync(AiProvider provider, SyncTrigger trigger, Exception failure, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var state = await db.ProviderSyncStates.SingleOrDefaultAsync(s => s.Provider == provider, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                state = new ProviderSyncState { Provider = provider };
                db.ProviderSyncStates.Add(state);
            }

            state.LastSyncAt = _time.GetUtcNow();
            state.LastStatus = SyncStatus.Failed;
            state.LastError = Truncate(failure.Message, 500);
            state.Trigger = trigger;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Catalog sync for {Provider} failed and the failure could not be recorded", provider);
        }
    }

    private static System.Text.Json.JsonElement CloneOrEmpty(System.Text.Json.JsonElement element) =>
        element.ValueKind == System.Text.Json.JsonValueKind.Undefined ? JsonValues.EmptyObject : element.Clone();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
