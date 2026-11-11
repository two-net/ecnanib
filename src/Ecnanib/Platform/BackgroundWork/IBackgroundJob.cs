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

/// <summary>One run of a job.</summary>
/// <param name="ScheduledFor">
/// The time the run was due, in UTC. For a job on UTC boundaries it is the boundary itself, such
/// as the close of a candle. The run starts a moment later, or much later when it was overdue.
/// </param>
public sealed record BackgroundJobRun(DateTimeOffset ScheduledFor);
