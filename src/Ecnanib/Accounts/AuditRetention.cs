using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Accounts;

/// <summary>
/// Deletes the events of the audit trail that are kept no longer: those of longer ago than the
/// days of <c>Retention:AuditDays</c> (NFR-DAT-5). See docs/retention.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An event goes by its age, whatever it records,</b> that of an operator as that of a user.
/// </para>
/// <para>
/// <b>Deleting one records nothing.</b> The trail says what a user or an operator did, and no
/// one did this: the log of the job says how many went.
/// </para>
/// <para>
/// <b>It works across users.</b> A job has no user who is signed in, and a row is past its time
/// whoever it is of. See "What is not asked for by owner" in docs/ownership.md.
/// </para>
/// </remarks>
internal sealed class AuditRetention(
    IDbContextFactory<EcnanibDbContext> contexts,
    RetentionSettings settings,
    TimeProvider clock)
{
    /// <summary>Deletes what is kept no longer, in small batches, and says how many events that was.</summary>
    /// <param name="cancellationToken">Stops the run between two batches. What was deleted by then stays deleted.</param>
    public Task<int> RemoveAgedAsync(CancellationToken cancellationToken)
    {
        // An event that is exactly as old as they are kept stays: it goes when it is older.
        var before = clock.GetUtcNow().AddDays(-settings.AuditDays);

        return AgedRows.DeleteAsync<AuditEvent>(
            contexts,
            events => events
                .Where(audited => audited.OccurredAt < before)
                .OrderBy(audited => audited.OccurredAt),
            cancellationToken);
    }
}

/// <summary>
/// Deletes the events of the audit trail that are kept no longer, every hour (NFR-DAT-5). See
/// <see cref="AuditRetention"/> and docs/retention.md.
/// </summary>
/// <remarks>
/// A run that is done again finds what the one before it left. Most runs delete what an hour
/// brought past its time.
/// </remarks>
internal sealed class AuditRetentionJob(AuditRetention retention, ILogger<AuditRetentionJob> logger) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "audit-retention";

    /// <summary>The time between two runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public async Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var deleted = await retention.RemoveAgedAsync(cancellationToken);

        if (deleted > 0)
        {
            logger.LogInformation("{Events} events of the audit trail were deleted, which were kept no longer", deleted);
        }
    }
}
