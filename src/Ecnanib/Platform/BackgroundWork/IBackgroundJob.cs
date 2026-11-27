namespace Ecnanib.Platform.BackgroundWork;

/// <summary>
/// A piece of scheduled work, such as a catalog sync or a retention job. The platform runs it on
/// its schedule, in one instance at a time, and keeps in the database when it last ran, so the
/// schedule goes on after a restart (NFR-ARC-4). A component registers its jobs with
/// <see cref="BackgroundJobs.AddBackgroundJob{TJob}"/>. See docs/background-work.md.
/// </summary>
/// <remarks>
/// A job is created once and lives as long as the application. It takes what it needs for a run
/// inside the run: a database context from the context factory, for example.
/// </remarks>
public interface IBackgroundJob
{
    /// <summary>
    /// What the job is called, in its state and in the log. It is the key of the job's row, so
    /// it is unique and stays the same from version to version: a job under a new name has never run.
    /// </summary>
    string Name { get; }

    /// <summary>When the job runs. It is read once, when the application starts.</summary>
    BackgroundJobSchedule Schedule { get; }

    /// <summary>
    /// Does the work once. A run can be cut short anywhere, by a stop or by a failure, so it
    /// leaves its data consistent wherever it ends, and a later run finds what is left to do.
    /// </summary>
    /// <param name="run">What this run is for.</param>
    /// <param name="cancellationToken">
    /// Canceled when the application stops, or stops being the instance that runs the background
    /// work. The job then ends soon, by throwing <see cref="OperationCanceledException"/>.
    /// </param>
    Task RunAsync(BackgroundJobRun run, CancellationToken cancellationToken);
}

/// <summary>
/// A job on an interval that can be due when the background work starts, although its interval
/// has not passed: a catalog sync when the catalog is empty, for example. A job implements this
/// beside <see cref="IBackgroundJob"/>. See docs/background-work.md.
/// </summary>
/// <remarks>
/// <para>
/// The scheduler asks once each time this instance takes up the background work, under the lock
/// that the work runs under, and before any job starts. It does not ask a job that is due
/// anyway, nor one whose latest run never finished: that one waits until it is next due, as
/// always.
/// </para>
/// <para>
/// Only a job on an interval can be due this way. A job on UTC boundaries runs for a boundary,
/// and the start of the work is none, so the application does not start with one that asks.
/// </para>
/// </remarks>
public interface IDueAtStart
{
    /// <summary>
    /// Whether the job has work to do now that the background work starts. An error that leaves
    /// this is logged, and the job then runs when it is next due.
    /// </summary>
    /// <param name="cancellationToken">Canceled when the application stops, or loses the lock.</param>
    Task<bool> IsDueAtStartAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A job on an interval that the background work starts with: its run comes first, and no other
/// job starts until it has ended (NFR-REL-3). What reconciles the records with the exchange is
/// such a job: nothing is scheduled before it has run. A job implements this beside
/// <see cref="IBackgroundJob"/>. See "A job that runs first" in docs/background-work.md.
/// </summary>
/// <remarks>
/// <para>
/// Each time this instance takes up the background work, at the start of the application and
/// again when the lock was lost and taken back, the scheduler runs the jobs that run first, one
/// after the other in the order that they were registered in, each as a run like any other. It
/// runs them whatever their rows say: also one whose interval has not passed, and one whose
/// latest run never finished.
/// </para>
/// <para>
/// The other jobs wait for them for a limited time. When that is over, they start beside the
/// runs that still go on.
/// </para>
/// <para>
/// Only a job on an interval can run first. A job on UTC boundaries runs for a boundary, and
/// the start of the work is none, so the application does not start with one that implements this.
/// </para>
/// </remarks>
public interface IRunsFirst
{
    /// <summary>
    /// Says that this instance takes up the background work now. It is called under the lock
    /// that the work runs under, before any job starts: what the job remembers of the work that
    /// it did before is void, because another instance may have done the work meanwhile. An
    /// error that leaves this is logged, and the job runs first all the same.
    /// </summary>
    void TakenUp();
}

/// <summary>
/// A job that has something to do last, as the application stops: what must not be left undone
/// until the next start, such as the protection of a position that a stopped cycle left without
/// one (NFR-REL-5). A job implements this beside <see cref="IBackgroundJob"/>. See "A job that
/// runs last" in docs/background-work.md.
/// </summary>
/// <remarks>
/// It is called once, by the instance that runs the background work, when the application
/// stops: after every run of every job has ended, and before the lock is let go. It is not
/// called when the lock is lost, and not by an instance that stands by. It is no run of the job:
/// nothing of it is in the job's row.
/// </remarks>
public interface IRunsLast
{
    /// <summary>
    /// Does what is to be done before the application has stopped. An error that leaves this is
    /// logged, and the application stops all the same.
    /// </summary>
    /// <param name="cancellationToken">
    /// Canceled when the time that the application has to stop in is nearly over. Nothing more
    /// is begun after that, and what is under way then is brought to its end.
    /// </param>
    Task RunLastAsync(CancellationToken cancellationToken);
}

/// <summary>One run of a job.</summary>
/// <param name="ScheduledFor">
/// The time the run was due, in UTC. For a job on UTC boundaries it is the boundary itself, such
/// as the close of a candle. The run starts a moment later, or much later when it was overdue.
/// </param>
public sealed record BackgroundJobRun(DateTimeOffset ScheduledFor);
