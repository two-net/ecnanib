namespace Ecnanib.Platform.Contracts;

/// <summary>
/// When a recurring job runs next. A schedule keeps no state: the last start comes from the database,
/// so restarting the application doesn't restart the clock. A module can derive its own.
/// </summary>
public abstract class JobSchedule
{
    /// <summary>The next time to run, which may be in the past. Missed runs collapse into one.</summary>
    /// <param name="lastStart">When the job last started, whatever the outcome. Null if it never has.</param>
    /// <param name="now">The current time.</param>
    public abstract DateTimeOffset NextRun(DateTimeOffset? lastStart, DateTimeOffset now);

    /// <summary>Runs at once if the job has never run, then this long after each start.</summary>
    public static JobSchedule Every(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        return new Interval(interval);
    }

    private sealed class Interval(TimeSpan interval) : JobSchedule
    {
        public override DateTimeOffset NextRun(DateTimeOffset? lastStart, DateTimeOffset now) =>
            lastStart + interval ?? now;
    }
}
