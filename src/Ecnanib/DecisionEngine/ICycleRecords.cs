using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// Records a cycle and the calls of its roles (FR-TEAM-8). See "The record of a cycle" in
/// docs/team.md.
/// </summary>
/// <remarks>
/// It is public so that a test can put a stand-in behind it, for cycles of agents that need no
/// rows: the tests see no internal type.
/// </remarks>
public interface ICycleRecords
{
    /// <summary>
    /// Stores a cycle with the calls of its roles, all of them or none: a cycle is recorded
    /// once, when it ends.
    /// </summary>
    /// <param name="cycle">The cycle, with how it ended.</param>
    /// <param name="outputs">The calls of its roles, in the order they were made. Empty when none was made.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task StoreAsync(DecisionCycle cycle, IReadOnlyList<RoleOutput> outputs, CancellationToken cancellationToken);
}

/// <summary>Stores a cycle and the calls of its roles in one transaction.</summary>
internal sealed class CycleRecords(IDbContextFactory<EcnanibDbContext> contexts) : ICycleRecords
{
    public async Task StoreAsync(DecisionCycle cycle, IReadOnlyList<RoleOutput> outputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(outputs);

        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        context.Add(cycle);
        context.AddRange(outputs);

        // One save is one transaction: the cycle and its calls are stored together.
        await context.SaveChangesAsync(cancellationToken);
    }
}
