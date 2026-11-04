using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Secrets;

/// <summary>
/// Checks the stored secrets against the configured keys, and starts re-encryption when the active
/// key has changed (FR-KEY-08). The host builds every hosted service before it starts any, and this
/// one takes <see cref="MasterKeys"/>, so an invalid key stops startup before any migration runs.
/// Registered straight after DatabaseStartup, so its StartingAsync runs on a migrated database.
/// </summary>
internal sealed class SecretStartup(
    MasterKeys keys,
    IDbContextFactory<EcnanibDbContext> contextFactory,
    IAuditTrail audit,
    IJobRunner jobs,
    SecretReencryption reencryption,
    IHostApplicationLifetime lifetime,
    ILogger<SecretStartup> logger) : IHostedLifecycleService
{
    /// <summary>Not the recurring job's name, so this run doesn't move that job's schedule.</summary>
    public const string JobName = "secret-reencryption-on-start";

    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(30);

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var secretsByKey = await SecretStore.CountByKeyAsync(context, cancellationToken);

            RefuseKeysThatAreMissing(secretsByKey);
            await RecordActivationAsync(context, cancellationToken);

            if (secretsByKey.Keys.Any(id => id != keys.Active.Id))
            {
                // Started, because RunAsync only works once the runner has started, and a failed
                // start should run nothing. A run never throws, so the task is safe to discard.
                lifetime.ApplicationStarted.Register(() => _ = jobs.RunAsync(JobName, JobTimeout, reencryption.RunAsync));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogCritical(exception, "Checking the stored secrets against the encryption keys failed. The application will not start.");
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A key removed too early would leave its secrets unreadable, so it must be put back first.</summary>
    private void RefuseKeysThatAreMissing(Dictionary<string, long> secretsByKey)
    {
        var missing = secretsByKey.Where(usage => !keys.TryGet(usage.Key, out _)).ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                string.Concat(missing.Select(usage =>
                    $"{MasterKeys.Section} has no key with Id \"{usage.Key}\", which {usage.Value} stored secrets still use. "))
                + $"Put it back, and keep it until re-encryption with \"{keys.Active.Id}\" has finished.");
        }
    }

    /// <summary>
    /// Records the active key when it differs from the last start's, and audits the change (FR-AUD-01).
    /// The first key isn't a rotation, so it's only recorded.
    /// </summary>
    private async Task RecordActivationAsync(EcnanibDbContext context, CancellationToken cancellationToken)
    {
        var active = keys.Active.Id;
        var previous = await context.Set<MasterKeyActivation>()
            .OrderByDescending(activation => activation.Id)
            .Select(activation => activation.KeyId)
            .FirstOrDefaultAsync(cancellationToken);

        if (previous == active)
        {
            return;
        }

        context.Set<MasterKeyActivation>().Add(new MasterKeyActivation { KeyId = active, ActivatedAt = DateTimeOffset.UtcNow });

        if (previous is not null)
        {
            audit.Add(context, new AuditEntry
            {
                Action = AuditAction.MasterKeyRotated,
                ActorKind = AuditActorKind.System,
                TargetType = "MasterKey",
                TargetId = active,
                Changes = [new AuditChange("ActiveKeyId", previous, active)],
            });

            logger.LogInformation("The active encryption key changed from {PreviousKeyId} to {KeyId}", previous, active);
        }

        // One save, so the activation and its audit record commit together.
        await context.SaveChangesAsync(cancellationToken);
    }
}
