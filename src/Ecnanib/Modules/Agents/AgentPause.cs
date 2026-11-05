using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// The system's pause of an Active agent, for a reason of its own: its credentials (FR-KEY-10, FR-KEY-11) or a
/// retired model (FR-CAT-04). Its positions and protective orders stay on the exchange (FR-AGT-06), and resuming
/// is the user's choice.
/// </summary>
internal static class AgentPause
{
    /// <summary>
    /// A conditional update on the status and version the caller read, with its audit record by the system
    /// (FR-AGT-04). Run it in the caller's transaction, under the user's lock, then save and commit.
    /// </summary>
    /// <returns>False when the agent changed since, so nothing was done.</returns>
    public static async Task<bool> TryAsync(
        EcnanibDbContext context,
        IAuditTrail audit,
        Guid userId,
        Guid agentId,
        int version,
        AgentStatusReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var updated = await context.Set<Agent>()
            .Where(row => row.Id == agentId && row.UserId == userId && row.Version == version && row.Status == AgentStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, AgentStatus.Paused)
                    .SetProperty(row => row.StatusReason, reason)
                    .SetProperty(row => row.StatusChangedAt, now)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);

        if (updated == 0)
        {
            return false;
        }

        audit.Add(
            context,
            AgentAudit.BySystem(AuditAction.AgentPaused, userId, agentId, AgentAudit.StatusChange(AgentStatus.Active, AgentStatus.Paused, reason)));

        return true;
    }
}
