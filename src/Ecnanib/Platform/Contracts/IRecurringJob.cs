namespace Ecnanib.Platform.Contracts;

/// <summary>
/// Work that runs on a schedule. Register it as a singleton in <c>Add&lt;Module&gt;()</c>. The runner
/// gives each job its own loop, so a job never overlaps itself and can't hold up another.
/// See docs/architecture.md.
/// </summary>
public interface IRecurringJob
{
    /// <summary>Must stay the same across releases: the schedule finds the job's last run by this name.</summary>
    string Name { get; }

    JobSchedule Schedule { get; }

    /// <summary>How long a run may take. <see cref="Timeout.InfiniteTimeSpan"/> means there's no limit.</summary>
    TimeSpan Timeout { get; }

    /// <summary>
    /// One run. Returning means it finished, so a run that stops early must throw. See
    /// <see cref="IJobRunner.RunAsync"/> for the tokens.
    /// </summary>
    Task RunAsync(JobContext context, CancellationToken cancellationToken);
}
