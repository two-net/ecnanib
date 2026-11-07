using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Experience;

/// <summary>
/// Deletes an agent's metrics and attributions with it, in the delete's own transaction. It takes the user's
/// <see cref="ExperienceLock"/> first, so a build under way finishes before, and one that starts after sees the agent gone.
/// </summary>
internal sealed class ExperienceHistory : IAgentHistory
{
    public async Task DeleteAsync(EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await ExperienceLock.TakeAsync(context, userId, cancellationToken);

        await context.Set<PerformanceSnapshotRow>()
            .Where(row => row.UserId == userId && row.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Set<CycleAttributionRow>()
            .Where(row => row.UserId == userId && row.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
