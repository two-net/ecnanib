using Ecnanib.Kernel;
using Ecnanib.Modules.Credentials.Contracts;
using Ecnanib.Modules.Notifications.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Credentials;

/// <summary>
/// Checks a saved key with its provider again and writes the result (FR-KEY-04, FR-KEY-11), for a user on
/// demand and for <see cref="CredentialRevalidationJob"/> on a schedule. A check claims the credential first,
/// so each one is checked at most once a minute whoever asks. When the provider can't be reached, nothing
/// changes, so an outage never pauses an agent. A key that stops being valid, or loses a market, raises
/// <see cref="NotificationType.CredentialInvalid"/> in the transaction that records it, and Agents pauses the
/// agents that used it when the observers hear. See Credentials in docs/architecture.md.
/// </summary>
internal sealed class CredentialChecks(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    ISecretStore secrets,
    IAuditTrail audit,
    INotifications notifications,
    AiValidator aiValidator,
    BinanceValidator binanceValidator,
    CredentialObservers observers,
    TimeProvider time,
    ILogger<CredentialChecks> logger)
{
    /// <summary>The shortest gap between two checks of one credential.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>Where the notification sends the user.</summary>
    public const string ListPage = "/credentials";

    /// <param name="actor">The user on demand, or the system on a schedule, which the audit record says.</param>
    public async Task<ValidationResult> CheckAsync(Guid userId, Guid id, AuditActorKind actor, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = now - Interval;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // One update claims the check, so of two at once only one reaches the provider.
        var claimed = await context.Set<Credential>()
            .Where(row => row.Id == id && row.UserId == userId && (row.CheckedAt == null || row.CheckedAt <= due))
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CheckedAt, now), cancellationToken);

        var row = await FindRowAsync(context, userId, id, cancellationToken);

        if (row is null)
        {
            return new ValidationResult(ValidationOutcome.NotFound);
        }

        if (claimed == 0)
        {
            return new ValidationResult(ValidationOutcome.TooSoon, row.ToRecord());
        }

        var verdict = await ValidateAsync(row, cancellationToken);

        if (!verdict.Reached)
        {
            return new ValidationResult(ValidationOutcome.Unreachable, row.ToRecord());
        }

        var spotEnabled = verdict.SpotEnabled ?? row.SpotEnabled;
        var futuresEnabled = verdict.FuturesEnabled ?? row.FuturesEnabled;
        int updated;

        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            // Only while the key checked is still the credential's: a rotation meanwhile has its own result.
            updated = await context.Set<Credential>()
                .Where(current => current.Id == row.Id && current.UserId == userId && current.ApiKeyId == row.ApiKeyId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(current => current.Status, verdict.Status)
                        .SetProperty(current => current.Problem, verdict.Problem)
                        .SetProperty(current => current.SpotEnabled, spotEnabled)
                        .SetProperty(current => current.FuturesEnabled, futuresEnabled)
                        .SetProperty(current => current.IpRestricted, verdict.IpRestricted ?? row.IpRestricted)
                        .SetProperty(current => current.ExchangeUid, row.ExchangeUid ?? verdict.Uid)
                        .SetProperty(current => current.ValidatedAt, now),
                    cancellationToken);

            if (updated == 1)
            {
                audit.Add(context, Entry(row, actor, [new(CredentialAuditFields.Status, row.Status.ToString(), verdict.Status.ToString())]));

                // A key that stopped working, or lost a market, pauses the agents that use it (FR-KEY-11).
                if (Failed(row, verdict.Status, spotEnabled, futuresEnabled))
                {
                    notifications.Add(
                        context,
                        new Notice(NotificationType.CredentialInvalid, userId, ListPage, row.Label, demo: row.Environment == TradingEnvironment.Demo));
                }

                await context.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Validated credential {CredentialId} for user {UserId} by {Actor}: {Provider} {Status} {Problem}",
            row.Id, userId, actor, row.Provider, verdict.Status, verdict.Problem);

        if (updated == 1)
        {
            observers.Notify(row, CredentialChangeKind.Validated, verdict.Status, row.Status);
        }

        var current = await FindRowAsync(context, userId, id, cancellationToken);

        return current is null ? new ValidationResult(ValidationOutcome.NotFound) : new ValidationResult(ValidationOutcome.Checked, current.ToRecord());
    }

    /// <summary>Whether a valid key stopped being valid, or a market it could trade on was turned off.</summary>
    public static bool Failed(Credential before, CredentialStatus status, bool? spotEnabled, bool? futuresEnabled) =>
        before.Status == CredentialStatus.Valid
        && (status != CredentialStatus.Valid
            || (before.SpotEnabled == true && spotEnabled == false)
            || (before.FuturesEnabled == true && futuresEnabled == false));

    /// <summary>Decrypts the credential's secrets for one check, and clears them after.</summary>
    private async Task<Verdict> ValidateAsync(Credential row, CancellationToken cancellationToken)
    {
        using var apiKey = await secrets.RevealAsync(row.ApiKeyId, cancellationToken);

        if (row.Kind == CredentialKind.Ai)
        {
            return await aiValidator.ValidateAsync(row.Provider, row.Id, apiKey, cancellationToken);
        }

        using var secret = await secrets.RevealAsync(
            row.ApiSecretId ?? throw new InvalidOperationException($"Credential {row.Id} has no secret."), cancellationToken);

        return await binanceValidator.ValidateAsync(
            row.Id,
            row.Environment ?? throw new InvalidOperationException($"Credential {row.Id} has no environment."),
            row.KeyType ?? BinanceKeyType.Hmac,
            apiKey,
            secret,
            cancellationToken);
    }

    private static AuditEntry Entry(Credential row, AuditActorKind actor, IReadOnlyList<AuditChange> changes)
    {
        var entry = actor == AuditActorKind.User
            ? AuditEntry.ByUser(AuditAction.CredentialValidated, row.UserId)
            : new AuditEntry { Action = AuditAction.CredentialValidated, ActorKind = actor, UserId = row.UserId };

        return entry with { TargetType = Credentials.Target, TargetId = row.Id.ToString(), Changes = changes };
    }

    private static Task<Credential?> FindRowAsync(EcnanibDbContext context, Guid userId, Guid id, CancellationToken cancellationToken) =>
        context.Set<Credential>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && row.UserId == userId, cancellationToken);
}

/// <summary>
/// Checks every key with its provider again, every <c>Security:CredentialRevalidationHours</c> (FR-KEY-11). A
/// key that stopped working raises a notification, and Agents pauses the agents that used it. A provider
/// that can't be reached changes nothing. Keys are checked a few at a time, each on its own, so one key's
/// failure never reaches another (NFR-REL-05).
/// </summary>
internal sealed class CredentialRevalidationJob(
    CredentialChecks checks,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    CredentialSettings settings,
    ILogger<CredentialRevalidationJob> logger) : IRecurringJob
{
    public const string JobName = "credential-revalidation";

    /// <summary>Few enough that every user's checks, which share the server's Binance weight, stay a trickle.</summary>
    private const int Parallelism = 4;

    public string Name => JobName;

    public JobSchedule Schedule { get; } = JobSchedule.Every(settings.RevalidationInterval);

    public TimeSpan Timeout => TimeSpan.FromHours(1);

    public async Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        List<(Guid UserId, Guid Id)> keys;

        await using (var database = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            keys = (await database.Set<Credential>()
                    .AsNoTracking()
                    .OrderBy(row => row.CreatedAt)
                    .ThenBy(row => row.Id)
                    .Select(row => new { row.UserId, row.Id })
                    .ToListAsync(cancellationToken))
                .Select(row => (row.UserId, row.Id))
                .ToList();
        }

        var counts = new int[Enum.GetValues<ValidationOutcome>().Length + 1];

        await Parallel.ForEachAsync(
            keys,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (key, token) =>
            {
                // Start nothing new once shutdown begins. A check under way finishes, because it can be an exchange call.
                if (context.Stopping.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var result = await checks.CheckAsync(key.UserId, key.Id, AuditActorKind.System, token);
                    Interlocked.Increment(ref counts[(int)result.Outcome]);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Interlocked.Increment(ref counts[^1]);
                    logger.LogError(exception, "Checking credential {CredentialId} again failed", key.Id);
                }
            });

        context.Details =
            $"Keys {keys.Count}, checked {counts[(int)ValidationOutcome.Checked]}, unreachable {counts[(int)ValidationOutcome.Unreachable]}, "
            + $"too soon {counts[(int)ValidationOutcome.TooSoon]}, gone {counts[(int)ValidationOutcome.NotFound]}, failed {counts[^1]}";

        context.Stopping.ThrowIfCancellationRequested();
    }
}
