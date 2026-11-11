using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.BackgroundWork;

/// <summary>Reads and writes the state of the jobs in the database.</summary>
/// <remarks>
/// Every write names the run it belongs to and changes nothing when the row has moved on. So an
/// instance that has lost the background work lock without knowing it yet can neither start a run
/// that another instance has started, nor overwrite what that instance recorded.
/// </remarks>
internal sealed class BackgroundJobStore(IDbContextFactory<EcnanibDbContext> contexts)
{
    /// <summary>The state of every job that has ever run, by name.</summary>
    public async Task<IReadOnlyDictionary<string, BackgroundJobState>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<BackgroundJobState>()
            .AsNoTracking()
            .ToDictionaryAsync(job => job.Name, StringComparer.Ordinal, cancellationToken);
    }

    /// <summary>The state of one job, or null when it has never run.</summary>
    public async Task<BackgroundJobState?> FindAsync(string name, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        return await context.Set<BackgroundJobState>()
            .AsNoTracking()
            .SingleOrDefaultAsync(job => job.Name == name, cancellationToken);
    }

    /// <summary>
    /// Records that a run starts, and says whether it may: only when the job's latest run is
    /// still the one the caller knows of.
    /// </summary>
    /// <param name="name">The job.</param>
    /// <param name="previousStart">The start of the latest run the caller knows of, or null when the job has never run.</param>
    /// <param name="startedAt">The start of the new run.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>False when another instance has started a run of the job since.</returns>
    public async Task<bool> TryStartAsync(
        string name, DateTimeOffset? previousStart, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        if (previousStart is null)
        {
            // The first run ever: whoever inserts the row has it.
            var inserted = await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO background_jobs (name, last_started_at) VALUES ({name}, {startedAt})
                ON CONFLICT (name) DO NOTHING
                """,
                cancellationToken);

            return inserted == 1;
        }

        var updated = await context.Set<BackgroundJobState>()
            .Where(job => job.Name == name && job.LastStartedAt == previousStart)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(job => job.LastStartedAt, startedAt)
                    .SetProperty(job => job.LastFinishedAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.LastOutcome, (BackgroundJobOutcome?)null),
                cancellationToken);

        return updated == 1;
    }

    /// <summary>Records how the run that started at <paramref name="startedAt"/> ended.</summary>
    /// <returns>False when the row is no longer about that run, so nothing was recorded.</returns>
    public async Task<bool> FinishAsync(
        string name,
        DateTimeOffset startedAt,
        DateTimeOffset finishedAt,
        BackgroundJobOutcome outcome,
        CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        var updated = await context.Set<BackgroundJobState>()
            .Where(job => job.Name == name && job.LastStartedAt == startedAt)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(job => job.LastFinishedAt, (DateTimeOffset?)finishedAt)
                    .SetProperty(job => job.LastOutcome, (BackgroundJobOutcome?)outcome),
                cancellationToken);

        return updated == 1;
    }
}
