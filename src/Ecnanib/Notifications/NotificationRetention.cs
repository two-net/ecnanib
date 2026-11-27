using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Ecnanib.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Notifications;

/// <summary>
/// Deletes the notifications that are kept no longer: those that were raised longer ago than
/// the days of <c>Retention:NotificationDays</c> (NFR-DAT-5). See docs/retention.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A notification goes by its age, read or not.</b> One that its user never read was there to
/// read for as long as any other.
/// </para>
/// <para>
/// <b>An email that was never sent is not sent for it.</b> The job that sends them gives a
/// notification up after a day, which is long before this.
/// </para>
/// <para>
/// <b>It works across users.</b> A job has no user who is signed in, and a row is past its time
/// whoever it is of. See "What is not asked for by owner" in docs/ownership.md.
/// </para>
/// </remarks>
internal sealed class NotificationRetention(
    IDbContextFactory<EcnanibDbContext> contexts,
    RetentionSettings settings,
    TimeProvider clock)
{
    /// <summary>Deletes what is kept no longer, in small batches, and says how many notifications that was.</summary>
    /// <param name="cancellationToken">Stops the run between two batches. What was deleted by then stays deleted.</param>
    public Task<int> RemoveAgedAsync(CancellationToken cancellationToken)
    {
        // A notification that is exactly as old as they are kept stays: it goes when it is older.
        var before = clock.GetUtcNow().AddDays(-settings.NotificationDays);

        return AgedRows.DeleteAsync<Notification>(
            contexts,
            notifications => notifications
                .Where(notification => notification.CreatedAt < before)
                .OrderBy(notification => notification.CreatedAt),
            cancellationToken);
    }
}

/// <summary>
/// Deletes the notifications that are kept no longer, every hour (NFR-DAT-5). See
/// <see cref="NotificationRetention"/> and docs/retention.md.
/// </summary>
/// <remarks>
/// A run that is done again finds what the one before it left. Most runs delete what an hour
/// brought past its time.
/// </remarks>
internal sealed class NotificationRetentionJob(NotificationRetention retention, ILogger<NotificationRetentionJob> logger)
    : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "notification-retention";

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
            logger.LogInformation("{Notifications} notifications were deleted, which were kept no longer", deleted);
        }
    }
}
