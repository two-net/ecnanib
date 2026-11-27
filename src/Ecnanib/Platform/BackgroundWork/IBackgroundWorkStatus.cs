namespace Ecnanib.Platform.BackgroundWork;

/// <summary>
/// How the scheduled work stands: each job with its schedule and its latest run, and whether a
/// run of it is overdue. It is the health of the scheduler that an operator status screen shows
/// (FR-OPS-2). See docs/background-work.md.
/// </summary>
/// <remarks>
/// It is read from the database, where the scheduler keeps the latest run of each job
/// (NFR-ARC-4), and from nothing that one instance holds in memory. So it says the same on every
/// instance, also on one that stands by and runs no job itself.
/// </remarks>
public interface IBackgroundWorkStatus
{
    /// <summary>
    /// The jobs of this version of the application, by name, and after them the jobs that the
    /// database has a run of and this version does not have.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<BackgroundJobStatus>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>What the database says of the latest run of a job.</summary>
public enum BackgroundJobRunState
{
    /// <summary>The job has not run yet: the database has no run of it.</summary>
    NoRunYet,

    /// <summary>
    /// The latest run started, and no end of it is recorded. It is still going, or the process
    /// ended before it did: the database cannot tell the two apart.
    /// </summary>
    NotEnded,

    /// <summary>The latest run did its work.</summary>
    Succeeded,

    /// <summary>The latest run failed with an error, which is in the log.</summary>
    Failed,

    /// <summary>The application stopped the latest run before the job was done.</summary>
    Stopped,
}

/// <summary>How one job stands.</summary>
/// <param name="Name">The job, by <see cref="IBackgroundJob.Name"/>.</param>
/// <param name="Schedule">
/// When the job runs, or null for a job that the database has a run of and this version of the
/// application does not have.
/// </param>
/// <param name="State">What the database says of its latest run.</param>
/// <param name="LastStartedAt">When its latest run started, or null when it has not run yet.</param>
/// <param name="LastFinishedAt">When that run ended, or null when no end of it is recorded.</param>
/// <param name="DueAt">
/// When the run after the latest one is, or was, due by the schedule, or null when that is not
/// known: the job has not run yet, or has no schedule here.
/// </param>
/// <param name="Overdue">
/// Whether that run is overdue: it was due longer ago than a run takes to start, and the database
/// has no start of it. A scheduler that runs starts a job within a second of when it is due, so a
/// job that is overdue says that no instance runs the background work, that the run before it
/// outlasts its schedule, or that the work has just started and the jobs that run first have
/// not ended yet: the other jobs wait for those, for two minutes at the most.
/// </param>
public sealed record BackgroundJobStatus(
    string Name,
    BackgroundJobSchedule? Schedule,
    BackgroundJobRunState State,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastFinishedAt,
    DateTimeOffset? DueAt,
    bool Overdue);
