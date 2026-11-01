using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Jobs;

/// <summary>The durable side of the runner. Each write is its own short-lived context.</summary>
internal sealed class JobRunStore(IDbContextFactory<EcnanibDbContext> contextFactory)
{
    public async Task<long> StartAsync(string jobName, string? correlationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var run = new JobRun
        {
            JobName = jobName,
            StartedAt = DateTimeOffset.UtcNow,
            Status = JobStatus.Running,
            CorrelationId = correlationId,
        };

        context.Add(run);
        await context.SaveChangesAsync(cancellationToken);

        return run.Id;
    }

    public async Task FinishAsync(
        long id,
        JobStatus status,
        string? details,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Set<JobRun>()
            .Where(run => run.Id == id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.Status, status)
                    .SetProperty(run => run.Details, details)
                    .SetProperty(run => run.FinishedAt, finishedAt),
                cancellationToken);
    }

    public async Task<DateTimeOffset?> LastStartAsync(string jobName, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Set<JobRun>()
            .Where(run => run.JobName == jobName)
            .MaxAsync(run => (DateTimeOffset?)run.StartedAt, cancellationToken);
    }

    /// <summary>
    /// With one instance (ASM-06), a run still marked Running when the application starts belongs to a
    /// process that's gone. It keeps a null end, because nobody knows when it stopped.
    /// </summary>
    public async Task InterruptAbandonedRunsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Set<JobRun>()
            .Where(run => run.Status == JobStatus.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.Status, JobStatus.Interrupted)
                    .SetProperty(run => run.Details, "The process ended before this run finished"),
                cancellationToken);
    }
}
