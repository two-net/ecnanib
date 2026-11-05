using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Platform.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Ecnanib.Modules.Agents;

/// <summary>
/// The system's pause of an Active agent, for a reason of its own: its credentials (FR-KEY-10, FR-KEY-11), a
/// retired model (FR-CAT-04) or a loss limit (FR-RISK-08). Its positions and protective orders stay on the
/// exchange (FR-AGT-06). Resuming is the user's choice, except after a daily or weekly loss limit, whose pause
/// ends on its own at <c>LossLimitUntil</c>.
/// </summary>
internal static class AgentPause
{
    /// <summary>
    /// A conditional update on the status and version the caller read, with its audit record by the system
    /// (FR-AGT-04). Run it in the caller's transaction, under the user's lock, then save and commit.
    /// </summary>
    /// <param name="lossLimitUntil">When a loss limit's pause ends, which activation waits for. Null for any other pause.</param>
    /// <returns>False when the agent changed since, so nothing was done.</returns>
    public static async Task<bool> TryAsync(
        EcnanibDbContext context,
        IAuditTrail audit,
        Guid userId,
        Guid agentId,
        int version,
        AgentStatusReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        DateTimeOffset? lossLimitUntil = null)
    {
        var updated = await context.Set<Agent>()
            .Where(row => row.Id == agentId && row.UserId == userId && row.Version == version && row.Status == AgentStatus.Active)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, AgentStatus.Paused)
                    .SetProperty(row => row.StatusReason, reason)
                    .SetProperty(row => row.StatusChangedAt, now)
                    .SetProperty(row => row.LossLimitUntil, lossLimitUntil)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);

        if (updated == 0)
        {
            return false;
        }

        var changes = AgentAudit.StatusChange(AgentStatus.Active, AgentStatus.Paused, reason);

        if (lossLimitUntil is { } until)
        {
            changes = [.. changes, new(AgentAuditFields.PausedUntil, null, until.ToString("O", CultureInfo.InvariantCulture))];
        }

        audit.Add(context, AgentAudit.BySystem(AuditAction.AgentPaused, userId, agentId, changes));

        return true;
    }
}
