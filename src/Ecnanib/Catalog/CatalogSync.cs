using System.Collections.Concurrent;
using Ecnanib.AiProviderGateway;
using Ecnanib.Platform.Logging;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Catalog;

/// <summary>
/// Syncs the catalog of one provider's models from the provider's own list: asks the gateway for
/// the list, stores what it says, and records the sync (FR-CAT-1 to FR-CAT-5, FR-CAT-7). It is
/// what the job, a request for a sync, and a user's key that its provider took all come to. It
/// also says which models a provider listed to a user's key, for Credentials to record
/// (FR-CAT-9). See docs/catalog.md.
/// </summary>
/// <remarks>
/// <para>
/// The provider is asked with no transaction open. The list is then stored in one transaction,
/// which begins by locking the provider's row, so two syncs of one provider store one after the
/// other, and a sync that fails leaves nothing of itself behind but the row that says so.
/// </para>
/// <para>
/// A list is the provider's whole catalog or the sync fails: the gateway hands on no part of
/// one. A model that a list lacks is counted as missed, and is marked unavailable when it was
/// missed often enough. With the keys of users it must also have gone unseen for long enough:
/// see <see cref="UnseenWithUsersKeys"/>. Whatever depends on a model is told in the transaction
/// that marks it, through <see cref="IModelDependents"/> (FR-CAT-8).
/// </para>
/// <para>
/// A key is handed to the gateway for the one list. Nothing here keeps it: the service's own is
/// read from the configuration file for each sync, and a user's comes with the call that tells
/// of it. A row of a sync names neither a user nor a key.
/// </para>
/// <para>
/// A user's key is asked for the list of its provider every time the provider took it. Where
/// the service has no key of its own for the provider, that list is the sync. Where it has one,
/// the list says only what the user's key can use, and nothing of it is stored here.
/// </para>
/// </remarks>
internal sealed class CatalogSync(
    IAiModelLists lists,
    AiProvidersSettings providers,
    CatalogSyncSettings settings,
    IDbContextFactory<EcnanibDbContext> contexts,
    IEnumerable<IModelDependents> dependents,
    TimeProvider clock,
    ILogger<CatalogSync> logger) : ICatalogSync
{
    /// <summary>
    /// How long a sync of one provider may take in all: the list, and storing it. It leaves the
    /// sync the time to record how it ended within the minute that it has (NFR-PRF-4).
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(55);

    /// <summary>
    /// How long a list may take that is asked with a user's key. It is the time a check of the
    /// key may take: a user waits for both with one form.
    /// </summary>
    public static readonly TimeSpan UserKeyLimit = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a provider is left alone after a list that was asked with a user's key ran out
    /// of time. The keys of many users are checked one after the other, so a provider that does
    /// not answer is waited for once in this time, and not once for every key.
    /// </summary>
    /// <remarks>
    /// Only a list that ran out of time starts it. One that failed otherwise failed at once, and
    /// may have failed for its key alone: a key that lists no model must not keep the keys that
    /// are checked after it, every day, from listing theirs.
    /// </remarks>
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    // How often Credentials checks a stored key again: daily (FR-KEY-3). The Catalog may not use
    // Credentials, so it says here what it goes by.
    private static readonly TimeSpan KeysAreCheckedEvery = TimeSpan.FromDays(1);

    // When a list that was asked with a user's key last ran out of time, by provider. In memory:
    // it is of no use after the pause, and another instance may ask for itself.
    private readonly ConcurrentDictionary<AiProviderCode, DateTimeOffset> _silentSince = new();

    /// <summary>
    /// How long no sync may have listed a model before a sync with a user's key can mark it
    /// unavailable: as many days as the syncs it may miss, and half a day (FR-CAT-5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// With the service's key, the syncs alone count: every list is the same key's view, so a
    /// model that it lacks often enough is gone. With the keys of users, a provider is synced as
    /// often as keys are checked, and a key may see fewer models than another. The number of
    /// syncs then says nothing: two checks of a key that sees less would take a model away from
    /// every user.
    /// </para>
    /// <para>
    /// What counts instead is how often the key that does see the model could have listed it,
    /// and each key is checked daily. So a model is marked only when no key has listed it for
    /// that many days, and half a day for the hour by which a daily check comes later each day.
    /// A model that one user's key alone lists stays through a check of that key that gave no
    /// list, as it stays through a sync that it misses on the schedule.
    /// </para>
    /// </remarks>
    public TimeSpan UnseenWithUsersKeys { get; } = KeysAreCheckedEvery * (settings.MissedSyncsBeforeUnavailable + 0.5);

    public async Task<IReadOnlyList<CatalogSyncRun>> SyncAsync(CancellationToken cancellationToken) =>
        await SyncAsync(CatalogSyncCause.OnDemand, cancellationToken);

    /// <summary>
    /// Syncs the catalog of every provider that the service has a key for, each on its own, so
    /// that one that is slow or fails neither delays nor fails another (FR-CAT-7).
    /// </summary>
    public async Task<IReadOnlyList<CatalogSyncRun>> SyncAsync(CatalogSyncCause cause, CancellationToken cancellationToken)
    {
        var synced = await SyncedWithTheServiceKeyAsync(cancellationToken);

        var syncs = await Task.WhenAll(synced.Select(provider =>
            SyncProviderAsync(provider.Code, provider.CatalogKey.Reveal(), cause, within: null, cancellationToken)));

        return [.. syncs.Select(sync => sync.Run)];
    }

    public async Task<IReadOnlyList<string>?> KeyValidatedAsync(
        AiProviderCode provider, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        if (!providers.ByCode.TryGetValue(provider, out var entry) || !lists.Providers.Contains(provider))
        {
            return null;
        }

        if (IsLeftAlone(provider))
        {
            return null;
        }

        // A provider that the service has a key for is synced with that one, on the schedule.
        // The user's key is then asked only for what it can use, and the catalog stays as it is.
        var (listed, timedOut) = entry.CatalogKey is null
            ? await SyncWithUsersKeyAsync(provider, apiKey, cancellationToken)
            : await ListForUsersKeyAsync(provider, apiKey, cancellationToken);

        if (timedOut)
        {
            _silentSince[provider] = clock.GetUtcNow();
        }
        else
        {
            _silentSince.TryRemove(provider, out _);
        }

        return listed;
    }

    public async Task<IReadOnlyList<ProviderSyncStatus>> StatusAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // The providers are the rows of their table, as the screen of the catalog lists them.
        var all = await context.Set<AiProvider>()
            .AsNoTracking()
            .OrderBy(provider => provider.Code)
            .ToListAsync(cancellationToken);

        var statuses = new List<ProviderSyncStatus>(all.Count);

        foreach (var provider in all)
        {
            // Each is the newest row of the provider's syncs, which their index finds: the table
            // has a row for every sync there ever was, and none of the others is read.
            var syncs = context.Set<CatalogSyncRun>().AsNoTracking().Where(sync => sync.Provider == provider.Code);

            var latest = await syncs
                .OrderByDescending(sync => sync.StartedAt)
                .ThenByDescending(sync => sync.Id)
                .FirstOrDefaultAsync(cancellationToken);

            // When the latest one failed, the catalog is as the last one that succeeded left it.
            var succeeded = latest is null or { Result: CatalogSyncResult.Succeeded }
                ? latest
                : await syncs
                    .Where(sync => sync.Result == CatalogSyncResult.Succeeded)
                    .OrderByDescending(sync => sync.StartedAt)
                    .ThenByDescending(sync => sync.Id)
                    .FirstOrDefaultAsync(cancellationToken);

            statuses.Add(new ProviderSyncStatus(provider.Code, provider.DisplayName, HasCatalogKey(provider.Code), latest, succeeded));
        }

        return statuses;
    }

    /// <summary>
    /// Whether the service has a key of its own that the catalog of a provider is synced with:
    /// the configuration file holds one, and the application can list the provider's models.
    /// </summary>
    private bool HasCatalogKey(AiProviderCode provider) =>
        providers.ByCode.TryGetValue(provider, out var entry) && entry.CatalogKey is not null && lists.Providers.Contains(provider);

    /// <summary>
    /// Whether a provider that the service has a key for has no model in the catalog: the
    /// catalog is then synced as the application starts, and not when it is next due (FR-CAT-2).
    /// </summary>
    public async Task<bool> IsEmptyAsync(CancellationToken cancellationToken)
    {
        var synced = await SyncedWithTheServiceKeyAsync(cancellationToken);
        if (synced.Count == 0)
        {
            return false;
        }

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        foreach (var (provider, _) in synced)
        {
            if (!await context.Set<AiModel>().AnyAsync(model => model.Provider == provider, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Syncs the catalog of a provider with a user's key, and says what the provider listed to
    /// it. A list that could not be stored was listed to the key all the same.
    /// </summary>
    private async Task<(IReadOnlyList<string>? Listed, bool TimedOut)> SyncWithUsersKeyAsync(
        AiProviderCode provider, string apiKey, CancellationToken cancellationToken)
    {
        var sync = await SyncProviderAsync(provider, apiKey, CatalogSyncCause.KeyValidated, UserKeyLimit, cancellationToken);

        return (sync.Listed, sync.Run.Failure is CatalogSyncFailure.TimedOut);
    }

    /// <summary>
    /// Asks a provider that the service has a key of its own for what it lists to a user's key.
    /// Nothing of the list is stored, and no sync is recorded: the catalog is the service's key's
    /// to fill. It ends with an error only when the caller cancels.
    /// </summary>
    private async Task<(IReadOnlyList<string>? Listed, bool TimedOut)> ListForUsersKeyAsync(
        AiProviderCode provider, string apiKey, CancellationToken cancellationToken)
    {
        // Everything logged for the list names the provider. The key is never given to the logger.
        using var asking = logger.BeginCorrelation(new Correlation(Provider: provider.Value));

        try
        {
            var list = await ListAsync(provider, apiKey, UserKeyLimit, cancellationToken);

            if (list.Outcome == AiModelListOutcome.Listed)
            {
                return (IdentifiersOf(list), false);
            }

            logger.LogWarning(
                "The AI provider {Provider} gave a user's key no list of its models: {Failure}. Which models the key can use is not known",
                provider.Value,
                FailureOf(list.Outcome));

            return (null, list.Outcome == AiModelListOutcome.TimedOut);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                error,
                "The AI provider {Provider} could not be asked for the models that it lists to a user's key, because of an error. Which models the key can use is not known",
                provider.Value);

            return (null, false);
        }
    }

    /// <summary>
    /// The providers that the configuration file holds a catalog key for, each with its key.
    /// Without any, nothing is asked of the database. A key for a provider whose models the
    /// application cannot list, or that is not one of its providers, is said and left out.
    /// </summary>
    private async Task<IReadOnlyList<(AiProviderCode Code, Secret CatalogKey)>> SyncedWithTheServiceKeyAsync(
        CancellationToken cancellationToken)
    {
        var withKey = new List<(AiProviderCode Code, Secret CatalogKey)>();
        foreach (var (code, entry) in providers.ByCode)
        {
            if (entry.CatalogKey is { } key)
            {
                withKey.Add((code, key));
            }
        }

        if (withKey.Count == 0)
        {
            return withKey;
        }

        List<AiProviderCode> known;
        await using (var context = await contexts.CreateDbContextAsync(cancellationToken))
        {
            known = await context.Set<AiProvider>().Select(provider => provider.Code).ToListAsync(cancellationToken);
        }

        var synced = new List<(AiProviderCode Code, Secret CatalogKey)>();
        foreach (var provider in withKey)
        {
            if (known.Contains(provider.Code) && lists.Providers.Contains(provider.Code))
            {
                synced.Add(provider);
            }
            else
            {
                logger.LogWarning(
                    "The configuration file holds a catalog key for {Provider}, which is not an AI provider whose models the application can list, so nothing is synced for it",
                    provider.Code.Value);
            }
        }

        return synced;
    }

    /// <summary>
    /// Syncs the catalog of one provider, and records the sync. It ends with an error only when
    /// the caller cancels: whatever else goes wrong is a failed sync, which is recorded and logged.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="apiKey">The key the list is asked with.</param>
    /// <param name="cause">Why the catalog is synced.</param>
    /// <param name="within">A shorter time for the list than the gateway gives it, or null.</param>
    /// <param name="cancellationToken">Cancels the sync.</param>
    private async Task<Synced> SyncProviderAsync(
        AiProviderCode provider,
        string apiKey,
        CatalogSyncCause cause,
        TimeSpan? within,
        CancellationToken cancellationToken)
    {
        // Everything logged for the sync names the provider. The key is never given to the logger.
        using var sync = logger.BeginCorrelation(new Correlation(Provider: provider.Value));

        var startedAt = Now();

        // What the provider listed, once it has: a list that could not be stored was listed all the same.
        IReadOnlyList<string>? listed = null;

        // The one limit on the sync as a whole, on the application's clock. The list has a
        // shorter one of its own, and what is left of this one is for storing it.
        using var limit = new CancellationTokenSource(Limit, clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);

        try
        {
            var list = await ListAsync(provider, apiKey, within, either.Token);

            if (list.Outcome != AiModelListOutcome.Listed)
            {
                return new Synced(
                    await NotSyncedAsync(provider, cause, startedAt, FailureOf(list.Outcome), cancellationToken), Listed: null);
            }

            listed = IdentifiersOf(list);

            var run = await StoreAsync(provider, cause, startedAt, list.Models, either.Token);

            logger.LogInformation(
                "The catalog of the AI provider {Provider} was synced with the {ModelCount} models that the provider lists: {Added} added, {Changed} changed, and {Unavailable} marked unavailable",
                provider.Value,
                list.Models.Count,
                run.Added,
                run.Changed,
                run.Unavailable);

            return new Synced(run, listed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limit.IsCancellationRequested)
        {
            // The sync ran out of its time, most likely while it waited to store the list behind
            // another sync of the provider. The caller did not cancel. The transaction is gone
            // with what it wrote.
            return new Synced(
                await NotSyncedAsync(provider, cause, startedAt, CatalogSyncFailure.TimedOut, cancellationToken), listed);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // The transaction that stored the list is gone with what it wrote.
            logger.LogError(
                error, "The catalog of the AI provider {Provider} was not synced, because of an error. The catalog stays as it is", provider.Value);

            return new Synced(
                await RecordFailureAsync(provider, cause, startedAt, CatalogSyncFailure.Error, cancellationToken), listed);
        }
    }

    /// <summary>Why a provider gave no list, in the word that a failed sync is recorded with.</summary>
    private static CatalogSyncFailure FailureOf(AiModelListOutcome outcome) => outcome switch
    {
        AiModelListOutcome.AuthenticationFailed => CatalogSyncFailure.KeyRefused,
        AiModelListOutcome.TimedOut => CatalogSyncFailure.TimedOut,
        _ => CatalogSyncFailure.NoList,
    };

    /// <summary>The identifiers of the models of a list, as the provider wrote them.</summary>
    private static IReadOnlyList<string> IdentifiersOf(AiModelList list) => [.. list.Models.Select(model => model.Id)];

    /// <summary>Says, and records, a sync that failed for something other than an error of the application.</summary>
    private Task<CatalogSyncRun> NotSyncedAsync(
        AiProviderCode provider,
        CatalogSyncCause cause,
        DateTimeOffset startedAt,
        CatalogSyncFailure failure,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "The catalog of the AI provider {Provider} was not synced: {Failure}. The catalog stays as it is", provider.Value, failure);

        return RecordFailureAsync(provider, cause, startedAt, failure, cancellationToken);
    }

    private async Task<AiModelList> ListAsync(
        AiProviderCode provider, string apiKey, TimeSpan? within, CancellationToken cancellationToken)
    {
        if (within is not { } limit)
        {
            return await lists.ListAsync(provider, apiKey, cancellationToken);
        }

        using var timer = new CancellationTokenSource(limit, clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);

        try
        {
            return await lists.ListAsync(provider, apiKey, either.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The list took longer than this limit. The caller did not cancel.
            return new AiModelList(AiModelListOutcome.TimedOut, []);
        }
    }

    /// <summary>Stores what a provider's list says, and the sync, in one transaction.</summary>
    private async Task<CatalogSyncRun> StoreAsync(
        AiProviderCode provider,
        CatalogSyncCause cause,
        DateTimeOffset startedAt,
        IReadOnlyList<ListedAiModel> listed,
        CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // The row of the provider, locked for the rest of the transaction, as the first thing
        // it asks: two syncs of one provider store one after the other. The lock is that of a
        // change, which a row that points at the provider does not wait for.
        var locked = await context.Database
            .SqlQuery<string>(
                $"""
                SELECT code AS "Value" FROM ai_providers
                WHERE code = {provider.Value}
                FOR NO KEY UPDATE
                """)
            .ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            throw new InvalidOperationException($"'{provider}' is not one of the application's AI providers, so its models cannot be stored.");
        }

        // Read after the lock, so that this is what the catalog holds until the transaction ends.
        var stored = await context.Set<AiModel>()
            .AsNoTracking()
            .Where(model => model.Provider == provider)
            .ToDictionaryAsync(model => model.Identifier, StringComparer.Ordinal, cancellationToken);

        var added = 0;
        var changed = 0;

        foreach (var model in listed)
        {
            if (!stored.TryGetValue(model.Id, out var known))
            {
                context.Set<AiModel>().Add(new AiModel(
                    Guid.CreateVersion7(),
                    provider,
                    model.Id,
                    model.DisplayName,
                    model.ContextTokens,
                    model.OutputTokens,
                    model.ReleasedAt,
                    model.DeprecatedAt,
                    model.RetiresAt,
                    AiModelStatus.Active,
                    missedSyncs: 0,
                    firstSeenAt: startedAt,
                    lastSeenAt: startedAt));
                added++;

                continue;
            }

            var saysOtherwise = SaysOtherwise(known, model);

            // A model that was gone and is listed again has changed too: it is active again.
            if (saysOtherwise || known.Status != AiModelStatus.Active)
            {
                changed++;
            }

            if (saysOtherwise)
            {
                await context.Set<AiModel>()
                    .Where(row => row.Id == known.Id)
                    .ExecuteUpdateAsync(
                        row => row
                            .SetProperty(stale => stale.DisplayName, model.DisplayName)
                            .SetProperty(stale => stale.ContextTokens, model.ContextTokens)
                            .SetProperty(stale => stale.OutputTokens, model.OutputTokens)
                            .SetProperty(stale => stale.ReleasedAt, model.ReleasedAt)
                            .SetProperty(stale => stale.DeprecatedAt, model.DeprecatedAt)
                            .SetProperty(stale => stale.RetiresAt, model.RetiresAt),
                        cancellationToken);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        var listedIds = listed.Select(model => model.Id).ToArray();

        // Every model of the list was seen by this sync, and is active (FR-CAT-5). A sync that
        // started later may have stored before this one: what it saw stands.
        await context.Set<AiModel>()
            .Where(model => model.Provider == provider && listedIds.Contains(model.Identifier))
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(model => model.LastSeenAt, model => model.LastSeenAt > startedAt ? model.LastSeenAt : startedAt)
                    .SetProperty(model => model.MissedSyncs, 0)
                    .SetProperty(model => model.Status, AiModelStatus.Active),
                cancellationToken);

        // Every other model of the provider was missed by it. The row stays: a model is never deleted.
        await context.Set<AiModel>()
            .Where(model => model.Provider == provider && !listedIds.Contains(model.Identifier))
            .ExecuteUpdateAsync(row => row.SetProperty(model => model.MissedSyncs, model => model.MissedSyncs + 1), cancellationToken);

        // Missed often enough: the provider has it no more (FR-CAT-5).
        var missedOftenEnough = context.Set<AiModel>()
            .Where(model => model.Provider == provider
                && model.Status == AiModelStatus.Active
                && model.MissedSyncs >= settings.MissedSyncsBeforeUnavailable);

        // With the key of a user, only when no key has listed it for long enough either: this
        // key may see fewer models than another.
        if (cause == CatalogSyncCause.KeyValidated)
        {
            var notSeenSince = startedAt - UnseenWithUsersKeys;
            missedOftenEnough = missedOftenEnough.Where(model => model.LastSeenAt <= notSeenSince);
        }

        var gone = await missedOftenEnough
            .Select(model => new { model.Id, model.Identifier })
            .ToListAsync(cancellationToken);

        if (gone.Count > 0)
        {
            var goneIds = gone.Select(model => model.Id).ToArray();

            // What depends on the models is told before they are marked, in this transaction:
            // what it changes is stored when they are marked, and not when they are not. A model
            // is told of once, when it becomes unavailable (FR-CAT-8).
            foreach (var dependent in dependents)
            {
                await dependent.ModelsUnavailableAsync(context, provider, goneIds, cancellationToken);
            }

            await context.Set<AiModel>()
                .Where(model => goneIds.Contains(model.Id))
                .ExecuteUpdateAsync(row => row.SetProperty(model => model.Status, AiModelStatus.Unavailable), cancellationToken);
        }

        var run = new CatalogSyncRun(
            Guid.CreateVersion7(),
            provider,
            cause,
            startedAt,
            Now(),
            CatalogSyncResult.Succeeded,
            failure: null,
            added,
            changed,
            unavailable: gone.Count);

        context.Set<CatalogSyncRun>().Add(run);
        await context.SaveChangesAsync(cancellationToken);

        // Not canceled: a commit that is asked for is waited for, so that what follows knows
        // whether the list is stored.
        await transaction.CommitAsync(CancellationToken.None);

        foreach (var model in gone)
        {
            logger.LogInformation(
                "The model {Model} of the AI provider {Provider} is marked unavailable: the provider has not listed it in the last {MissedSyncs} syncs or more",
                model.Identifier,
                provider.Value,
                settings.MissedSyncsBeforeUnavailable);
        }

        return run;
    }

    /// <summary>Whether a provider now says something else about a model than the catalog holds.</summary>
    private static bool SaysOtherwise(AiModel known, ListedAiModel listed) =>
        known.DisplayName != listed.DisplayName
        || known.ContextTokens != listed.ContextTokens
        || known.OutputTokens != listed.OutputTokens
        || known.ReleasedAt != listed.ReleasedAt
        || known.DeprecatedAt != listed.DeprecatedAt
        || known.RetiresAt != listed.RetiresAt;

    /// <summary>
    /// Records a sync that failed, in a transaction of its own: the catalog is as it was, and the
    /// row says that it was tried (FR-CAT-7). A row that cannot be written is said in the log.
    /// </summary>
    private async Task<CatalogSyncRun> RecordFailureAsync(
        AiProviderCode provider,
        CatalogSyncCause cause,
        DateTimeOffset startedAt,
        CatalogSyncFailure failure,
        CancellationToken cancellationToken)
    {
        var run = new CatalogSyncRun(
            Guid.CreateVersion7(),
            provider,
            cause,
            startedAt,
            Now(),
            CatalogSyncResult.Failed,
            failure,
            added: 0,
            changed: 0,
            unavailable: 0);

        try
        {
            await using var context = await contexts.CreateDbContextAsync(cancellationToken);

            context.Set<CatalogSyncRun>().Add(run);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                error, "The failed sync of the catalog of the AI provider {Provider} could not be recorded", provider.Value);
        }

        return run;
    }

    /// <summary>Whether a list for a user's key ran out of time so lately that the provider is not asked again yet.</summary>
    private bool IsLeftAlone(AiProviderCode provider)
    {
        if (!_silentSince.TryGetValue(provider, out var since))
        {
            return false;
        }

        // A time that lies ahead, because the clock was set back since, keeps nothing waiting.
        var passed = clock.GetUtcNow() - since;

        return passed >= TimeSpan.Zero && passed < Pause;
    }

    /// <summary>
    /// The time, to the microsecond, which is what the database keeps. So a time that was
    /// written and read back equals the one in memory.
    /// </summary>
    private DateTimeOffset Now()
    {
        var ticks = clock.GetUtcNow().UtcTicks;

        return new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMicrosecond), TimeSpan.Zero);
    }

    /// <summary>A sync of one provider, with what the provider listed for it.</summary>
    /// <param name="Run">The sync, as it is recorded.</param>
    /// <param name="Listed">
    /// The identifiers of the models that the provider listed, or null when it gave no list. A
    /// list that could not be stored is here all the same.
    /// </param>
    private sealed record Synced(CatalogSyncRun Run, IReadOnlyList<string>? Listed);
}
