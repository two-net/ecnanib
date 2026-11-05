using System.Collections.Concurrent;
using Ecnanib.Kernel;
using Ecnanib.Modules.AiAdapters.Contracts;
using Ecnanib.Modules.Catalog.Contracts;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Lists the models of users' valid AI keys and merges them into the catalog, with what each key can use
/// (FR-CAT-02). Every trigger runs the same pipeline: each provider on its own, its keys fetched with no lock
/// held, then written in one transaction with the run's record (FR-CAT-08). A sync of everyone's keys also
/// deprecates and retires the models no key lists any more (FR-CAT-04). See Model catalog in
/// docs/architecture.md.
/// </summary>
internal sealed class CatalogSync(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ICredentialKeys credentials,
    IAiProviders providers,
    INotifications notifications,
    IJobRunner jobs,
    ICorrelationContext correlation,
    CatalogSettings settings,
    ModelStatusObservers observers,
    TimeProvider time,
    ILogger<CatalogSync> logger) : ICatalogSync
{
    /// <summary>An administrator's sync of every key. Not the schedule's name, so it doesn't move the schedule.</summary>
    public const string AllJobName = "catalog-sync-all";

    public const string UserJobName = "catalog-sync-user";

    public const string CredentialJobName = "catalog-sync-credential";

    /// <summary>Where administrators see the runs.</summary>
    public const string FailureLink = "/admin/catalog";

    public static readonly TimeSpan FullTimeout = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan OnDemandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest one key's list may take, retries and pages included, so a provider that hangs costs each
    /// key this much rather than holding the run until its timeout.
    /// </summary>
    public static readonly TimeSpan KeyTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The shortest gap between two of one user's syncs on demand.</summary>
    public static readonly TimeSpan UserInterval = TimeSpan.FromMinutes(1);

    /// <summary>Keys of one provider asked at once. Each provider also has its own limit per key.</summary>
    private const int KeysAtOnce = 4;

    /// <summary>Held by a sync of everyone's keys, so the schedule and an administrator never overlap.</summary>
    private readonly SemaphoreSlim _everyone = new(1, 1);

    /// <summary>Held while writing, so writes can't interleave. There's one instance (ASM-06).</summary>
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary>When each user last synced on demand, kept only for <see cref="UserInterval"/>.</summary>
    private readonly Dictionary<Guid, DateTimeOffset> _userSyncs = [];

    public async Task<UserSyncResult> SyncUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var keys = await credentials.ListAiAsync(userId, cancellationToken);

        if (!keys.Any(key => key.Status == CredentialStatus.Valid && IsEnabled(key.Provider)))
        {
            return new UserSyncResult(UserSyncOutcome.NoKeys);
        }

        if (!Claim(userId))
        {
            return new UserSyncResult(UserSyncOutcome.TooSoon);
        }

        Summary? summary = null;
        var status = await jobs.RunAsync(UserJobName, OnDemandTimeout, async (context, token) =>
        {
            summary = await SyncAsync(CatalogSyncTrigger.User, keys, userId, context, token);
            context.Details = summary.Describe();
        });

        return status == JobStatus.Succeeded && summary is { } done
            ? new UserSyncResult(UserSyncOutcome.Synced, done.Keys, done.KeysFailed, done.Models)
            : new UserSyncResult(UserSyncOutcome.Failed);
    }

    /// <summary>
    /// Starts an administrator's sync of every user's valid keys in the background, unless one is running. Through
    /// <see cref="CatalogAdministration"/>, which audits it.
    /// </summary>
    public SyncAllOutcome StartEveryone()
    {
        // Only a hint for the answer. The run takes the guard itself, so a run the runner refuses can't leave
        // it held, and of two requests at once the second run records that it was already running.
        if (_everyone.CurrentCount == 0)
        {
            return SyncAllOutcome.AlreadyRunning;
        }

        // RunAsync never throws for the work's failure, so the task can be left to run.
        _ = jobs.RunAsync(AllJobName, FullTimeout, (context, token) => SyncEveryoneAsync(CatalogSyncTrigger.Administrator, context, token));

        return SyncAllOutcome.Started;
    }

    /// <summary>Every user's valid keys, then a tidy of rows that lost events left behind.</summary>
    public async Task SyncEveryoneAsync(CatalogSyncTrigger trigger, JobContext context, CancellationToken cancellationToken)
    {
        if (!await _everyone.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            context.Details = "Already running";
            return;
        }

        try
        {
            var keys = await credentials.ListAllAiAsync(cancellationToken);
            context.Details = (await SyncAsync(trigger, keys, owner: null, context, cancellationToken)).Describe();
        }
        finally
        {
            _everyone.Release();
        }
    }

    /// <summary>What a change to one credential calls for, decided by <see cref="CatalogCredentialObserver"/>.</summary>
    public async Task ApplyAsync(CredentialChange change, CredentialAction action, JobContext context, CancellationToken cancellationToken)
    {
        if (action == CredentialAction.Sync)
        {
            if (!IsEnabled(change.Provider))
            {
                context.Details = "Provider disabled";
                return;
            }

            // The write checks the key is still valid, so a key that changed again since is left alone.
            AiCredential[] keys = [new(change.CredentialId, change.UserId, change.Provider, CredentialStatus.Valid)];
            context.Details = (await SyncAsync(CatalogSyncTrigger.Credential, keys, change.UserId, context, cancellationToken)).Describe();
            return;
        }

        var rows = await ClearAsync(change.UserId, change.CredentialId, remove: action == CredentialAction.Remove, cancellationToken);
        context.Details = $"{rows} rows";
    }

    /// <param name="owner">Whose keys these are, or null for everyone's, which also tidies rows of keys that are gone.</param>
    private async Task<Summary> SyncAsync(
        CatalogSyncTrigger trigger, IReadOnlyList<AiCredential> keys, Guid? owner, JobContext context, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        var valid = keys.Where(key => key.Status == CredentialStatus.Valid).ToList();

        // Everyone's sync visits every enabled provider, even one with no valid keys left, to tidy its rows.
        var names = providers.All
            .Where(provider => provider.Enabled)
            .Select(provider => provider.Name)
            .Where(name => owner is null || valid.Any(key => key.Provider == name))
            .ToList();

        var results = await Task.WhenAll(names.Select(name => SyncProviderAsync(
            name, trigger, [.. valid.Where(key => key.Provider == name)], owner, startedAt, context, cancellationToken)));

        return Summary.Of(results);
    }

    private async Task<ProviderResult> SyncProviderAsync(
        string provider,
        CatalogSyncTrigger trigger,
        IReadOnlyList<AiCredential> keys,
        Guid? owner,
        DateTimeOffset syncStartedAt,
        JobContext context,
        CancellationToken cancellationToken)
    {
        // Recorded before anything is asked, so a run that's interrupted still has its row (FR-CAT-08).
        var runId = await StartRunAsync(provider, trigger, keys.Count, cancellationToken);
        var fetched = new ConcurrentBag<Fetched>();

        // Shutdown stops the fetching: an AI call is safe to abandon, and nothing is written until the end.
        await Parallel.ForEachAsync(
            keys,
            new ParallelOptions { MaxDegreeOfParallelism = KeysAtOnce, CancellationToken = context.Stopping },
            async (key, _) => fetched.Add(await FetchAsync(key, context, cancellationToken)));

        var results = fetched.OrderBy(result => result.Key.Id).ToList();
        var written = await WriteAsync(runId, provider, results, owner, syncStartedAt, cancellationToken);

        logger.LogInformation(
            "Synced {Provider} models ({Trigger}): {Keys} keys, {KeysFailed} failed, {Added} added, {Updated} updated, "
            + "{Deprecated} deprecated, {Retired} retired",
            provider, trigger, written.Keys, written.KeysFailed, written.Added, written.Updated, written.Deprecated, written.Retired);

        foreach (var change in written.Changes)
        {
            logger.LogInformation(
                "{Provider} model {ModelId} is now {Status}, from {PreviousStatus}", provider, change.ModelId, change.Status, change.PreviousStatus);
        }

        // Only once committed, so Agents never acts on a change that rolled back.
        observers.Notify(provider, written.Changes);

        // Only when the provider looks down to everyone, so one user's revoked or unpaid key never alerts anybody.
        if (trigger is CatalogSyncTrigger.Schedule or CatalogSyncTrigger.Administrator
            && results.Count > 0
            && results.All(result => result.ProviderSide))
        {
            await notifications.RaiseForAdministratorsAsync(
                new AdministratorNotice(NotificationType.CatalogSyncFailed, FailureLink, provider), cancellationToken);
        }

        return written;
    }

    /// <summary>One key's list. Whatever goes wrong stays with this key (NFR-REL-05), except shutdown.</summary>
    private async Task<Fetched> FetchAsync(AiCredential credential, JobContext context, CancellationToken cancellationToken)
    {
        var startedAt = time.GetUtcNow();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(context.Stopping);
        limit.CancelAfter(KeyTimeout);

        try
        {
            var key = await credentials.OpenAiKeyToListModelsAsync(credential.UserId, credential.Id, cancellationToken);

            try
            {
                return new Fetched(credential, startedAt, Models: await providers.ListModelsAsync(key, limit.Token));
            }
            finally
            {
                key.ApiKey.Dispose();
            }
        }
        catch (CredentialUnusableException exception)
        {
            return new Fetched(credential, startedAt, Unusable: exception.Reason);
        }
        catch (AiException exception)
        {
            logger.LogWarning(
                "Listing {Provider} models with credential {CredentialId} failed: {Failure} {StatusCode}",
                credential.Provider, credential.Id, exception.Failure, exception.StatusCode);

            return new Fetched(credential, startedAt, Failure: exception.Failure, StatusCode: exception.StatusCode);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !context.Stopping.IsCancellationRequested)
        {
            logger.LogWarning(
                "Listing {Provider} models with credential {CredentialId} took longer than {Seconds} seconds",
                credential.Provider, credential.Id, KeyTimeout.TotalSeconds);

            return new Fetched(credential, startedAt, Failure: AiFailure.Timeout);
        }
        catch (Exception exception) when (!context.Stopping.IsCancellationRequested)
        {
            logger.LogError(exception, "Listing {Provider} models with credential {CredentialId} failed", credential.Provider, credential.Id);

            return new Fetched(credential, startedAt, Failure: AiFailure.Other);
        }
    }

    private async Task<long> StartRunAsync(string provider, CatalogSyncTrigger trigger, int keys, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var run = new CatalogSyncRun
        {
            Provider = provider,
            Trigger = trigger,
            StartedAt = time.GetUtcNow(),
            Keys = keys,
            CorrelationId = correlation.CurrentId,
        };

        context.Set<CatalogSyncRun>().Add(run);
        await context.SaveChangesAsync(cancellationToken);

        return run.Id;
    }

    /// <summary>
    /// Writes one provider's results and closes its run, in one transaction. It takes the run's own token, not
    /// <see cref="JobContext.Stopping"/>, so results already fetched aren't dropped as shutdown begins.
    /// </summary>
    private async Task<ProviderResult> WriteAsync(
        long runId,
        string provider,
        IReadOnlyList<Fetched> results,
        Guid? owner,
        DateTimeOffset syncStartedAt,
        CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken);

        try
        {
            // Read again inside the lock, so a key deleted or invalidated while its list was fetched doesn't get
            // its rows back. A deletion's own clean-up takes the same lock.
            var keys = owner is { } user
                ? await credentials.ListAiAsync(user, cancellationToken)
                : await credentials.ListAllAiAsync(cancellationToken);
            var current = keys.Where(key => key.Provider == provider).ToDictionary(key => key.Id);
            var now = time.GetUtcNow();

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // Before the rows are loaded, so nothing is deleted twice.
            if (owner is null)
            {
                await TidyAsync(context, provider, current, cancellationToken);
            }

            var changes = new List<ModelStatusChange>();
            var (added, updated, listed) = await MergeModelsAsync(context, provider, results, now, changes, cancellationToken);
            await RecordAvailabilityAsync(context, provider, results, current, cancellationToken);

            // Only everyone's keys can show that nobody can use a model, and only when at least one of them answered.
            var (deprecated, retired) = owner is null && results.Any(result => result.Models is not null)
                ? await AgeAsync(context, provider, listed, now, changes, cancellationToken)
                : (0, 0);

            var failed = results.Where(result => result.Failure is not null).ToList();
            var run = await context.Set<CatalogSyncRun>().SingleAsync(row => row.Id == runId, cancellationToken);
            run.FinishedAt = now;
            run.KeysFailed = failed.Count;
            run.ModelsAdded = added;
            run.ModelsUpdated = updated;
            run.ModelsDeprecated = deprecated;
            run.ModelsRetired = retired;
            run.Errors = SyncError.ToJson([.. failed.Select(result => new SyncError(result.Key.Id, result.Failure!.Value, result.StatusCode))]);

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ProviderResult(results.Count, failed.Count, added, updated, deprecated, retired, listed, changes);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// The union of every list. Each field takes the first value a key reported, in credential order, so the
    /// result doesn't depend on which key answered first, and a value no key sent this time is kept. A model
    /// that was deprecated or retired is Active again once any key lists it, which counts as an update.
    /// </summary>
    private static async Task<(int Added, int Updated, IReadOnlySet<string> Listed)> MergeModelsAsync(
        EcnanibDbContext context,
        string provider,
        IReadOnlyList<Fetched> results,
        DateTimeOffset now,
        List<ModelStatusChange> changes,
        CancellationToken cancellationToken)
    {
        var merged = new Dictionary<string, AiModel>(StringComparer.Ordinal);

        foreach (var model in results.SelectMany(result => result.Models ?? []))
        {
            merged[model.Id] = merged.TryGetValue(model.Id, out var first) ? Combine(first, model) : model;
        }

        if (merged.Count == 0)
        {
            return (0, 0, new HashSet<string>());
        }

        var stored = await context.Set<Model>()
            .Where(model => model.Provider == provider)
            .ToDictionaryAsync(model => model.ModelId, StringComparer.Ordinal, cancellationToken);
        var added = 0;
        var updated = 0;

        foreach (var model in merged.Values)
        {
            if (stored.TryGetValue(model.Id, out var row))
            {
                var changed = Apply(row, model);

                if (row.Status is ModelStatus.Deprecated or ModelStatus.Retired)
                {
                    changes.Add(new ModelStatusChange(provider, row.ModelId, ModelStatus.Active, row.Status));
                    row.Status = ModelStatus.Active;
                    changed = true;
                }

                updated += changed ? 1 : 0;
                row.LastSeenAt = now;
                row.MissingSince = null;
            }
            else
            {
                context.Set<Model>().Add(New(provider, model, now));
                added++;
            }
        }

        return (added, updated, merged.Keys.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Each key that listed gets its models marked available and its others not. Newer results win: a key whose
    /// rows were written by a sync that began asking later is left as it is.
    /// </summary>
    private static async Task RecordAvailabilityAsync(
        EcnanibDbContext context,
        string provider,
        IReadOnlyList<Fetched> results,
        IReadOnlyDictionary<Guid, AiCredential> current,
        CancellationToken cancellationToken)
    {
        var ids = results.Select(result => result.Key.Id).ToList();
        var rows = (await context.Set<ModelAvailability>()
                .Where(row => row.Provider == provider && ids.Contains(row.CredentialId))
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.CredentialId);

        foreach (var result in results)
        {
            var mine = rows[result.Key.Id].ToList();

            if (mine.Any(row => row.CheckedAt > result.StartedAt))
            {
                continue;
            }

            if (result.Models is { } models)
            {
                // Deleted or invalidated while its list was fetched.
                if (current.GetValueOrDefault(result.Key.Id) is not { Status: CredentialStatus.Valid })
                {
                    continue;
                }

                var listed = models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);

                foreach (var row in mine)
                {
                    row.Available = listed.Contains(row.ModelId);
                    row.CheckedAt = result.StartedAt;
                }

                foreach (var modelId in listed.Except(mine.Select(row => row.ModelId), StringComparer.Ordinal))
                {
                    context.Set<ModelAvailability>().Add(new ModelAvailability
                    {
                        CredentialId = result.Key.Id,
                        Provider = provider,
                        ModelId = modelId,
                        UserId = result.Key.UserId,
                        Available = true,
                        CheckedAt = result.StartedAt,
                    });
                }
            }
            else if (result.Unusable is CredentialUnusableReason.NotFound)
            {
                context.Set<ModelAvailability>().RemoveRange(mine);
            }
            else if (result.Unusable is not null)
            {
                foreach (var row in mine)
                {
                    row.Available = false;
                    row.CheckedAt = result.StartedAt;
                }
            }

            // A key the provider didn't answer for keeps what it had, as Credentials keeps a key's status in an
            // outage, so a provider's bad hour can't make models look unavailable.
        }
    }

    /// <summary>
    /// After a sync of everyone's keys, a model that no key listed and that no key can use any more is missing:
    /// Deprecated the first time, and Retired once it has been missing for Catalog:RetireAfterMissingDays
    /// (FR-CAT-04). A key that failed keeps its rows, so a model any failing key could use stays as it is, and
    /// an outage can't deprecate anything. The tidy has already dropped what deleted and invalid keys had.
    /// Hiding is an override in <c>model_overrides</c>, so a hidden model ages like any other.
    /// </summary>
    private async Task<(int Deprecated, int Retired)> AgeAsync(
        EcnanibDbContext context,
        string provider,
        IReadOnlySet<string> listed,
        DateTimeOffset now,
        List<ModelStatusChange> changes,
        CancellationToken cancellationToken)
    {
        // So the query below sees the availability this run recorded.
        await context.SaveChangesAsync(cancellationToken);

        var missing = await context.Set<Model>()
            .Where(model => model.Provider == provider && (model.Status == ModelStatus.Active || model.Status == ModelStatus.Deprecated))
            .Where(model => !context.Set<ModelAvailability>()
                .Any(row => row.Provider == model.Provider && row.ModelId == model.ModelId && row.Available))
            .ToListAsync(cancellationToken);
        var deprecated = 0;
        var retired = 0;

        // A model listed by a key deleted while it was fetched has no rows, but it was still listed.
        foreach (var model in missing.Where(model => !listed.Contains(model.ModelId)).OrderBy(model => model.ModelId, StringComparer.Ordinal))
        {
            if (model.Status == ModelStatus.Active)
            {
                model.Status = ModelStatus.Deprecated;
                model.MissingSince = now;
                changes.Add(new ModelStatusChange(provider, model.ModelId, ModelStatus.Deprecated, ModelStatus.Active));
                deprecated++;
            }
            else if (now - (model.MissingSince ??= now) >= settings.RetireAfter)
            {
                model.Status = ModelStatus.Retired;
                changes.Add(new ModelStatusChange(provider, model.ModelId, ModelStatus.Retired, ModelStatus.Deprecated));
                retired++;
            }
        }

        return (deprecated, retired);
    }

    /// <summary>
    /// The schedule is the recovery path for the events Credentials raises (Inversions in docs/architecture.md):
    /// the rows of a key that's gone are deleted, and those of a key that isn't valid are marked unavailable.
    /// </summary>
    private static async Task TidyAsync(
        EcnanibDbContext context, string provider, IReadOnlyDictionary<Guid, AiCredential> current, CancellationToken cancellationToken)
    {
        var known = current.Keys.ToList();
        var valid = current.Values.Where(key => key.Status == CredentialStatus.Valid).Select(key => key.Id).ToList();

        await context.Set<ModelAvailability>()
            .Where(row => row.Provider == provider && !known.Contains(row.CredentialId))
            .ExecuteDeleteAsync(cancellationToken);

        await context.Set<ModelAvailability>()
            .Where(row => row.Provider == provider && row.Available && !valid.Contains(row.CredentialId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Available, false), cancellationToken);
    }

    /// <summary>A deleted key loses its rows, and one that stopped being valid has them marked unavailable.</summary>
    private async Task<int> ClearAsync(Guid userId, Guid credentialId, bool remove, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await _writes.WaitAsync(cancellationToken);

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var rows = context.Set<ModelAvailability>().Where(row => row.CredentialId == credentialId && row.UserId == userId);

            return remove
                ? await rows.ExecuteDeleteAsync(cancellationToken)
                : await rows.Where(row => row.Available).ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.Available, false).SetProperty(row => row.CheckedAt, now),
                    cancellationToken);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Claims the user's sync, unless they had one within <see cref="UserInterval"/>.</summary>
    private bool Claim(Guid userId)
    {
        var now = time.GetUtcNow();

        lock (_userSyncs)
        {
            foreach (var stale in _userSyncs.Where(entry => now - entry.Value >= UserInterval).Select(entry => entry.Key).ToList())
            {
                _userSyncs.Remove(stale);
            }

            return _userSyncs.TryAdd(userId, now);
        }
    }

    private bool IsEnabled(string provider) => providers.All.Any(info => info.Name == provider && info.Enabled);

    private static AiModel Combine(AiModel first, AiModel next) => first with
    {
        DisplayName = first.DisplayName ?? next.DisplayName,
        CreatedAt = first.CreatedAt ?? next.CreatedAt,
        ContextWindow = first.ContextWindow ?? next.ContextWindow,
        MaxOutputTokens = first.MaxOutputTokens ?? next.MaxOutputTokens,
        Family = first.Family ?? next.Family,
        Capabilities = new AiCapabilities(
            first.Capabilities.ImageInput ?? next.Capabilities.ImageInput,
            first.Capabilities.StructuredOutput ?? next.Capabilities.StructuredOutput,
            first.Capabilities.ToolUse ?? next.Capabilities.ToolUse,
            first.Capabilities.LiveSearch ?? next.Capabilities.LiveSearch,
            first.Capabilities.ReasoningEffort ?? next.Capabilities.ReasoningEffort),
    };

    private static Model New(string provider, AiModel model, DateTimeOffset now) => new()
    {
        Provider = provider,
        ModelId = model.Id,
        DisplayName = model.DisplayName,
        Family = model.Family,

        // PostgreSQL stores an instant, and Npgsql takes only UTC.
        ReleasedAt = model.CreatedAt?.ToUniversalTime(),
        ContextWindow = model.ContextWindow,
        MaxOutputTokens = model.MaxOutputTokens,
        ImageInput = model.Capabilities.ImageInput,
        StructuredOutput = model.Capabilities.StructuredOutput,
        ToolUse = model.Capabilities.ToolUse,
        LiveSearch = model.Capabilities.LiveSearch,
        ReasoningEffort = model.Capabilities.ReasoningEffort,
        FirstSeenAt = now,
        LastSeenAt = now,
    };

    /// <summary>Copies what the provider sent, and says whether anything changed. A field it didn't send keeps its value.</summary>
    private static bool Apply(Model row, AiModel model)
    {
        var before = Snapshot(row);
        var capabilities = model.Capabilities;

        row.DisplayName = model.DisplayName ?? row.DisplayName;
        row.Family = model.Family ?? row.Family;
        row.ReleasedAt = model.CreatedAt?.ToUniversalTime() ?? row.ReleasedAt;
        row.ContextWindow = model.ContextWindow ?? row.ContextWindow;
        row.MaxOutputTokens = model.MaxOutputTokens ?? row.MaxOutputTokens;
        row.ImageInput = capabilities.ImageInput ?? row.ImageInput;
        row.StructuredOutput = capabilities.StructuredOutput ?? row.StructuredOutput;
        row.ToolUse = capabilities.ToolUse ?? row.ToolUse;
        row.LiveSearch = capabilities.LiveSearch ?? row.LiveSearch;
        row.ReasoningEffort = capabilities.ReasoningEffort ?? row.ReasoningEffort;

        return before != Snapshot(row);
    }

    private static (string?, string?, DateTimeOffset?, int?, int?, (bool?, bool?, bool?, bool?, bool?)) Snapshot(Model row) =>
        (row.DisplayName, row.Family, row.ReleasedAt, row.ContextWindow, row.MaxOutputTokens,
            (row.ImageInput, row.StructuredOutput, row.ToolUse, row.LiveSearch, row.ReasoningEffort));

    /// <summary>One key's outcome: its models, or why there are none.</summary>
    /// <param name="StartedAt">When it began asking, which decides whose result is newer.</param>
    private sealed record Fetched(
        AiCredential Key,
        DateTimeOffset StartedAt,
        IReadOnlyList<AiModel>? Models = null,
        AiFailure? Failure = null,
        int? StatusCode = null,
        CredentialUnusableReason? Unusable = null)
    {
        /// <summary>
        /// A failure that's the provider's rather than the key's: a timeout, an overload, or a server error or
        /// answer it couldn't read. Not a refused key, a rate limit, or a key with no money left.
        /// </summary>
        public bool ProviderSide =>
            Failure is AiFailure.Timeout or AiFailure.Overloaded
            || (Failure is AiFailure.Other && StatusCode is not (>= 400 and < 500));
    }

    private sealed record ProviderResult(
        int Keys,
        int KeysFailed,
        int Added,
        int Updated,
        int Deprecated,
        int Retired,
        IReadOnlySet<string> Models,
        IReadOnlyList<ModelStatusChange> Changes);

    private sealed record Summary(int Providers, int Keys, int KeysFailed, int Added, int Updated, int Deprecated, int Retired, int Models)
    {
        public static Summary Of(IReadOnlyCollection<ProviderResult> results) => new(
            results.Count,
            results.Sum(result => result.Keys),
            results.Sum(result => result.KeysFailed),
            results.Sum(result => result.Added),
            results.Sum(result => result.Updated),
            results.Sum(result => result.Deprecated),
            results.Sum(result => result.Retired),
            results.Sum(result => result.Models.Count));

        /// <summary>Counts only, for <see cref="JobContext.Details"/>.</summary>
        public string Describe() =>
            $"{Providers} providers, {Keys} keys, {KeysFailed} failed, {Added} added, {Updated} updated, {Deprecated} deprecated, {Retired} retired";
    }
}
