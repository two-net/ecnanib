using System.Security.Cryptography;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Secrets;

/// <summary>
/// Re-encrypts every secret that an older key seals with the active key (FR-KEY-08). Startup runs it
/// when there's work, and <see cref="SecretReencryptionJob"/> runs it every hour in case that run
/// didn't finish. Only one runs at a time. See Secrets in docs/architecture.md.
/// </summary>
internal sealed class SecretReencryption(
    IDbContextFactory<EcnanibDbContext> contextFactory,
    SecretStore store,
    MasterKeys keys,
    ILogger<SecretReencryption> logger)
{
    private const int BatchSize = 100;

    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>
    /// A batch is safe to abandon, because it commits as one, so this stops on <see cref="JobContext.Stopping"/>.
    /// </summary>
    public async Task RunAsync(JobContext context, CancellationToken cancellationToken)
    {
        if (!await _running.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            context.Details = "Already running";
            return;
        }

        try
        {
            await ReencryptAsync(context, context.Stopping);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task ReencryptAsync(JobContext context, CancellationToken stopping)
    {
        var active = keys.Active.Id;
        var pending = await CountPendingAsync(stopping);
        var unreadable = new List<Guid>();
        var done = 0;

        if (pending > 0)
        {
            logger.LogInformation("Re-encrypting {Pending} secrets with key {KeyId}", pending, active);
        }

        while (pending > 0)
        {
            stopping.ThrowIfCancellationRequested();

            await using var database = await contextFactory.CreateDbContextAsync(stopping);

            var batch = await database.Set<Secret>()
                .Where(secret => secret.KeyId != active && !unreadable.Contains(secret.Id))
                .OrderBy(secret => secret.Id)
                .Take(BatchSize)
                .ToListAsync(stopping);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var secret in batch)
            {
                try
                {
                    store.Reseal(secret);
                }
                catch (CryptographicException exception)
                {
                    unreadable.Add(secret.Id);
                    logger.LogWarning(
                        exception,
                        "Secret {Id} couldn't be decrypted with key {KeyId}, so it stays encrypted with that key",
                        secret.Id,
                        secret.KeyId);
                }
            }

            try
            {
                done += await database.SaveChangesAsync(stopping);
            }
            catch (DbUpdateConcurrencyException)
            {
                // An owner replaced or deleted a row in this batch. Nothing in it was saved, and the
                // next query finds whatever is still left.
            }

            logger.LogInformation("Re-encrypted {Done} of {Pending} secrets", done, pending);
        }

        var remaining = await CountPendingAsync(stopping);

        context.Details = $"{done} re-encrypted, {unreadable.Count} unreadable, {remaining} remaining";

        if (remaining > 0)
        {
            logger.LogWarning(
                "{Remaining} secrets are still encrypted with older keys, so keep every configured key until they aren't",
                remaining);
        }
        else if (pending > 0)
        {
            logger.LogInformation(
                "Every secret is now encrypted with key {KeyId}, so the other keys can be removed from the configuration",
                active);
        }
    }

    private async Task<long> CountPendingAsync(CancellationToken cancellationToken)
    {
        await using var database = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await database.Set<Secret>().LongCountAsync(secret => secret.KeyId != keys.Active.Id, cancellationToken);
    }
}

/// <summary>
/// The recovery path for the run that startup begins, as Jobs in docs/architecture.md requires of
/// work started with RunAsync. With nothing to do, a run is one count.
/// </summary>
internal sealed class SecretReencryptionJob(SecretReencryption reencryption) : IRecurringJob
{
    public string Name => "secret-reencryption";

    public JobSchedule Schedule { get; } = JobSchedule.Every(TimeSpan.FromHours(1));

    public TimeSpan Timeout { get; } = TimeSpan.FromMinutes(30);

    public Task RunAsync(JobContext context, CancellationToken cancellationToken) =>
        reencryption.RunAsync(context, cancellationToken);
}
