using Ecnanib.Platform.BackgroundWork;
using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Availability;

/// <summary>
/// Says that the service is up, each time it is asked, so that its availability can be measured
/// (NFR-REL-1). The service is up while an instance runs the background work and the database
/// answers it: that is when cycles run and pages are answered from what is stored. See
/// "Availability" in docs/reliability.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An instance lengthens a row of its own.</b> Its first beat writes a row that begins when
/// the instance started, or when the service last said that it was up if that was later: an
/// instance that takes the work over from another goes on where that one left off. Each beat
/// after it moves the end of that row to now.
/// </para>
/// <para>
/// <b>A beat that comes too late begins a new row.</b> When the row was last lengthened longer
/// ago than <see cref="Tolerance"/>, the database did not answer or nothing ran the work
/// meanwhile, and the time since then is not claimed: the new row begins now, and the time
/// between the two rows counts as down.
/// </para>
/// <para>
/// <b>Nothing is done as the application stops.</b> A row ends with its last beat, which is a
/// minute before the stop at the most. So a stop is counted from a little too early, and never
/// from too late: the service is never said to have been up when it was not.
/// </para>
/// </remarks>
internal sealed class Uptime(
    IDbContextFactory<EcnanibDbContext> contexts,
    TimeProvider clock,
    ILogger<Uptime> logger)
{
    /// <summary>
    /// How long after a beat the service still counts as up without another. A beat is due each
    /// minute, and one that the scheduler starts late, or that fails once, is no outage.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(3);

    // When this instance started: it is made as the host starts the scheduler, which takes the jobs.
    private readonly DateTimeOffset _startedAt = clock.GetUtcNow();

    // The row of this instance, once it has written one.
    private Guid? _span;

    /// <summary>
    /// When a row says that the service was up until, asked at a moment: until that moment when
    /// its last beat is within <see cref="Tolerance"/> of it, because the next beat is still to
    /// come, and until its last beat otherwise.
    /// </summary>
    public static Stretch StretchOf(DateTimeOffset beganAt, DateTimeOffset beatAt, DateTimeOffset now) =>
        new(beganAt, beatAt >= now - Tolerance && now > beatAt ? now : beatAt);

    /// <summary>Says that the service is up now.</summary>
    public async Task BeatAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        if (_span is { } span)
        {
            var earliest = now - Tolerance;

            // Only while the row is recent: one statement, so a row that is too old is not lengthened over the break.
            var lengthened = await context.Set<UpSpan>()
                .Where(row => row.Id == span && row.BeatAt >= earliest)
                .ExecuteUpdateAsync(row => row.SetProperty(up => up.BeatAt, now), cancellationToken);

            if (lengthened > 0)
            {
                return;
            }
        }

        var last = await context.Set<UpSpan>()
            .AsNoTracking()
            .OrderByDescending(row => row.BeatAt)
            .Select(row => (DateTimeOffset?)row.BeatAt)
            .FirstOrDefaultAsync(cancellationToken);

        // The first row of an instance reaches back to its start, and no further than to where
        // another row ends. A row after a break of this instance begins now.
        var beganAt = _span is null && _startedAt < now
            ? (last is { } ended && ended > _startedAt ? Earlier(ended, now) : _startedAt)
            : now;

        var written = new UpSpan(Guid.CreateVersion7(), beganAt, now);

        context.Set<UpSpan>().Add(written);
        await context.SaveChangesAsync(cancellationToken);

        _span = written.Id;

        if (last is not { } before)
        {
            logger.LogInformation("The service says for the first time that it is up. Its availability is measured from {From}", beganAt);
        }
        else if (beganAt > before)
        {
            await SayBreakAsync(context, new Stretch(before, beganAt), cancellationToken);
        }
    }

    /// <summary>Says in the log that nothing said that the service was up for a time, and how much of that counts as down.</summary>
    private async Task SayBreakAsync(EcnanibDbContext context, Stretch gap, CancellationToken cancellationToken)
    {
        var announced = await context.Set<MaintenanceWindow>()
            .AsNoTracking()
            .Where(window => window.InAdvance && window.EndsAt > gap.From && window.StartsAt < gap.Until)
            .Select(window => new { window.StartsAt, window.EndsAt })
            .ToListAsync(cancellationToken);

        var down = Stretches.Length(
            Stretches.Without(gap, Stretches.Joined(announced.Select(window => new Stretch(window.StartsAt, window.EndsAt)))));

        if (down > TimeSpan.Zero)
        {
            logger.LogWarning(
                "Nothing said that the service was up from {From} to {Until}. {DownSeconds} s of that count as down: no maintenance was announced in advance for them",
                gap.From,
                gap.Until,
                (long)down.TotalSeconds);
        }
        else
        {
            logger.LogInformation(
                "Nothing said that the service was up from {From} to {Until}, during maintenance that was announced in advance. It does not count as down",
                gap.From,
                gap.Until);
        }
    }

    private static DateTimeOffset Earlier(DateTimeOffset one, DateTimeOffset other) => one < other ? one : other;
}

/// <summary>
/// Has the service say that it is up, every minute (NFR-REL-1). See <see cref="Uptime"/> and
/// "Availability" in docs/reliability.md.
/// </summary>
/// <remarks>
/// A run writes one row or lengthens one, and calls nothing outside the database. A run that
/// fails says nothing: when the runs fail for longer than <see cref="Uptime.Tolerance"/>, the
/// time counts as down, which it was for whoever needed the database.
/// </remarks>
internal sealed class UptimeJob(Uptime uptime) : IBackgroundJob
{
    /// <summary>The name of the job, which is the key of its row of the <c>background_jobs</c> table.</summary>
    public const string JobName = "uptime";

    /// <summary>The time between two beats.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public string Name => JobName;

    public BackgroundJobSchedule Schedule { get; } = BackgroundJobSchedule.Every(Interval);

    public Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        return uptime.BeatAsync(cancellationToken);
    }
}
