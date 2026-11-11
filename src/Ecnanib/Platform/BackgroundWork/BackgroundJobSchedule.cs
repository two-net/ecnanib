namespace Ecnanib.Platform.BackgroundWork;

/// <summary>
/// When a job runs. There are two kinds, and they differ in what becomes of the time that passed
/// while the application was down: an interval makes up for it with one run, and UTC boundaries
/// that passed are skipped.
/// </summary>
public sealed class BackgroundJobSchedule
{
    private readonly TimeSpan _period;
    private readonly bool _atUtcBoundaries;

    private BackgroundJobSchedule(TimeSpan period, bool atUtcBoundaries)
    {
        _period = period;
        _atUtcBoundaries = atUtcBoundaries;
    }

    /// <summary>
    /// Runs the job every <paramref name="interval"/>, measured from the start of its previous
    /// run. The previous run is in the database, so a restart neither starts the interval again
    /// nor runs the job on every start. A job that has never run is due at once, and so is one
    /// that became overdue while the application was down: once, however many intervals passed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The interval is not positive.</exception>
    public static BackgroundJobSchedule Every(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        return new BackgroundJobSchedule(interval, atUtcBoundaries: false);
    }

    /// <summary>
    /// Runs the job at every multiple of <paramref name="period"/> since midnight, in UTC: with
    /// 15 minutes, at 00:00, 00:15, 00:30, and so on. A boundary that passes while the application
    /// is down, or while the previous run is still going, is skipped and never made up for
    /// (FR-CYC-8). The run is told its boundary in <see cref="BackgroundJobRun.ScheduledFor"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The period is not positive, or does not divide a day into equal parts.
    /// </exception>
    public static BackgroundJobSchedule AtUtcBoundaries(TimeSpan period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        if (TimeSpan.TicksPerDay % period.Ticks != 0)
        {
            // Otherwise the boundaries would fall at other times each day.
            throw new ArgumentOutOfRangeException(nameof(period), period, "The period must divide a day into equal parts.");
        }

        return new BackgroundJobSchedule(period, atUtcBoundaries: true);
    }

    /// <summary>
    /// Whether a run that could not start when it was due is dropped once the following one is
    /// due. True on UTC boundaries, and false on an interval, where a late run is still the run.
    /// </summary>
    internal bool SkipsMissedRuns => _atUtcBoundaries;

    /// <summary>
    /// The time the job is next due. A time that is not after <paramref name="now"/> means it is
    /// due at once.
    /// </summary>
    /// <param name="previousStart">When the job's previous run started, or null when there is none to go by.</param>
    /// <param name="now">The current time.</param>
    public DateTimeOffset NextRun(DateTimeOffset? previousStart, DateTimeOffset now)
    {
        if (_atUtcBoundaries)
        {
            // Strictly after now: a moment that is exactly on a boundary waits for the following
            // one. Ticks count from a midnight in UTC, so the multiples of the period fall on
            // the same times every day.
            var ticks = now.UtcTicks;

            return new DateTimeOffset(ticks - (ticks % _period.Ticks) + _period.Ticks, TimeSpan.Zero);
        }

        if (previousStart is not { } start)
        {
            return now;
        }

        // A start that lies in the future, because the clock has been set back since, must not
        // keep the job waiting for longer than one interval.
        var next = start + _period;
        var latest = now + _period;

        return next < latest ? next : latest;
    }

    public override string ToString() =>
        _atUtcBoundaries ? $"at UTC boundaries of {_period}" : $"every {_period}";
}
