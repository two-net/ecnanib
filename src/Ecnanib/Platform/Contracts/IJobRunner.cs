namespace Ecnanib.Platform.Contracts;

/// <summary>Runs background work that isn't on a schedule. See docs/architecture.md.</summary>
public interface IJobRunner
{
    /// <summary>
    /// Runs <paramref name="work"/> now, on its own task, as one recorded run with its own correlation
    /// ID. Shutdown waits for it. The token it receives fires only at the timeout or when the time for
    /// shutting down runs out, not when shutdown begins, so it's safe to pass to an exchange call. Use
    /// <see cref="JobContext.Stopping"/> to notice that shutdown has begun.
    /// </summary>
    /// <param name="name">
    /// The job name to record. Use one that no recurring job has, because a recurring job's schedule
    /// reads the last start under its name.
    /// </param>
    /// <param name="timeout">
    /// How long the run may take. <see cref="Timeout.InfiniteTimeSpan"/> means there's no limit.
    /// </param>
    /// <param name="work">The work to run.</param>
    /// <returns>
    /// How the run ended, or <see cref="JobStatus.NotStarted"/>. A failing run doesn't throw, so the
    /// task is safe to discard. It completes when the work has really ended, which for work that
    /// ignores cancellation can be long after its status was recorded.
    /// </returns>
    Task<JobStatus> RunAsync(string name, TimeSpan timeout, Func<JobContext, CancellationToken, Task> work);
}
