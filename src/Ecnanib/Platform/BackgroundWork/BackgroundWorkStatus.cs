namespace Ecnanib.Platform.BackgroundWork;

/// <summary>
/// Says how the scheduled work stands, from the jobs that are registered and from the latest run
/// that the database keeps of each (FR-OPS-2). See <see cref="IBackgroundWorkStatus"/>.
/// </summary>
/// <remarks>
/// It only reads. When a run is due it works out as the scheduler does, from the start of the run
/// before it, and it asks the scheduler nothing: an instance that stands by answers as the one
/// that runs the jobs does.
/// </remarks>
internal sealed class BackgroundWorkStatus(
    IEnumerable<IBackgroundJob> jobs,
    BackgroundJobStore store,
    TimeProvider clock) : IBackgroundWorkStatus
{
    /// <summary>
    /// How long after it was due a run may still be on its way to starting, before it is said to
    /// be overdue. The scheduler looks at the clock every second, and an instance that takes the
    /// work over needs a few seconds to see that the other one is gone. The shortest schedule
    /// that the configuration file allows is as long as this.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<BackgroundJobStatus>> ReadAsync(CancellationToken cancellationToken)
    {
        var states = await store.LoadAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var registered = jobs
            .OrderBy(job => job.Name, StringComparer.Ordinal)
            .Select(job => Of(job.Name, job.Schedule, states.GetValueOrDefault(job.Name), now))
            .ToList();

        var known = registered.Select(job => job.Name).ToHashSet(StringComparer.Ordinal);

        // A job that an earlier version ran, or one that this instance was started without. Its
        // row is in the database all the same, and is said rather than left out.
        var others = states.Values
            .Where(state => !known.Contains(state.Name))
            .OrderBy(state => state.Name, StringComparer.Ordinal)
            .Select(state => Of(state.Name, schedule: null, state, now));

        return [.. registered, .. others];
    }

    private static BackgroundJobStatus Of(string name, BackgroundJobSchedule? schedule, BackgroundJobState? state, DateTimeOffset now)
    {
        if (state is null)
        {
            // Due at once, as the scheduler has it. Since when is not known, so it is not overdue.
            return new BackgroundJobStatus(
                name, schedule, BackgroundJobRunState.NoRunYet, LastStartedAt: null, LastFinishedAt: null, DueAt: null, Overdue: false);
        }

        var run = state.LastOutcome switch
        {
            BackgroundJobOutcome.Succeeded => BackgroundJobRunState.Succeeded,
            BackgroundJobOutcome.Failed => BackgroundJobRunState.Failed,
            BackgroundJobOutcome.Stopped => BackgroundJobRunState.Stopped,
            _ => BackgroundJobRunState.NotEnded,
        };

        var due = schedule is null ? (DateTimeOffset?)null : DueAfter(schedule, state);

        return new BackgroundJobStatus(
            name, schedule, run, state.LastStartedAt, state.LastFinishedAt, due, Overdue: due is { } at && now - at > Grace);
    }

    /// <summary>When the run after the latest one is due, by the rule of the scheduler.</summary>
    private static DateTimeOffset DueAfter(BackgroundJobSchedule schedule, BackgroundJobState state)
    {
        // A run on an interval that the application stopped left its work to do: the job is due
        // again from the moment that it was stopped. A boundary is not made up for, so a job on
        // boundaries waits for the one after its start whatever became of the run.
        if (!schedule.OnUtcBoundaries && state is { LastOutcome: BackgroundJobOutcome.Stopped, LastFinishedAt: { } stoppedAt })
        {
            return stoppedAt;
        }

        // One period after the start, or the boundary that follows it.
        return schedule.NextRun(state.LastStartedAt, state.LastStartedAt);
    }
}
