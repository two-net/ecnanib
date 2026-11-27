using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Platform.Persistence;

/// <summary>
/// Deletes the rows that are kept no longer, in small batches (NFR-DAT-5). The retention jobs of
/// the components delete with it: each says which rows of its own are past their time, and this
/// says how they go. See docs/retention.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A batch is a statement of its own, in a transaction of its own.</b> It holds its locks for
/// as long as <see cref="BatchSize"/> rows take, and what it deleted stays deleted when a later
/// batch fails or the run is stopped: the next run finds what is left.
/// </para>
/// <para>
/// <b>It goes on until nothing is left.</b> A run that stopped at a count would fall behind a
/// table that grows faster than the count, and nobody would be told.
/// </para>
/// <para>
/// It names no table: the platform must not name a component's types. What goes with a row by
/// its keys goes in the same statement, and is not counted.
/// </para>
/// </remarks>
public static class AgedRows
{
    /// <summary>The most rows that one statement deletes.</summary>
    public const int BatchSize = 500;

    /// <summary>
    /// Deletes every row that <paramref name="aged"/> gives, <see cref="BatchSize"/> at a time,
    /// in the order that it gives them in.
    /// </summary>
    /// <typeparam name="TRow">The entity whose rows are deleted.</typeparam>
    /// <param name="contexts">What gives a context for each batch.</param>
    /// <param name="aged">
    /// The rows that are past their time, among all rows of the entity, the oldest first: what
    /// it gives must be found by an index, since it is asked for once for every batch.
    /// </param>
    /// <param name="cancellationToken">Stops the run between two batches. What was deleted by then stays deleted.</param>
    /// <returns>How many rows were deleted.</returns>
    public static async Task<int> DeleteAsync<TRow>(
        IDbContextFactory<EcnanibDbContext> contexts,
        Func<IQueryable<TRow>, IOrderedQueryable<TRow>> aged,
        CancellationToken cancellationToken)
        where TRow : class
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(aged);

        var deleted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var context = await contexts.CreateDbContextAsync(cancellationToken);

            var batch = await aged(context.Set<TRow>())
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);

            deleted += batch;

            // A batch that is not full took the last of them.
            if (batch < BatchSize)
            {
                return deleted;
            }
        }
    }
}
