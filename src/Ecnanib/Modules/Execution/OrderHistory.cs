using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Execution;

/// <summary>
/// Deletes an agent's trade journal, orders and protections with it. A trade's fills, funding payments and cycles go
/// with it, through their foreign keys. Agents refuses while it has any open (FR-AGT-03).
/// </summary>
internal sealed class OrderHistory : IAgentHistory
{
    public async Task DeleteAsync(EcnanibDbContext context, Guid userId, Guid agentId, CancellationToken cancellationToken)
    {
        await context.Set<TradeRow>()
            .Where(row => row.UserId == userId && row.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Set<ProtectionRow>()
            .Where(row => row.UserId == userId && row.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);

        await context.Set<OrderRow>()
            .Where(row => row.UserId == userId && row.AgentId == agentId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
