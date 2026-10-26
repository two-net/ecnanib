using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// The open-position cap as the hard limits count it (FR-AGT-14, FR-EXE-04): positions and pending entries against the
/// smaller of the agent's own limit and the system cap. The council gate and the Risk Manager's limits check share it,
/// so the gate never skips a council the limits would have let open, nor asks one they would refuse (NFR-COST-01).
/// </summary>
public static class PositionRoom
{
    /// <summary>Open trades plus pending entry orders.</summary>
    public static int Open(IReadOnlyList<Trade> openTrades, IReadOnlyList<Order> openOrders)
    {
        ArgumentNullException.ThrowIfNull(openTrades);
        ArgumentNullException.ThrowIfNull(openOrders);
        return openTrades.Count + openOrders.Count(o => o.Purpose == OrderPurpose.Entry);
    }

    /// <summary>The smaller of the agent's limit and the system cap.</summary>
    public static int Cap(Agent agent, int systemCap)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return Math.Min(agent.RiskLimits.MaxOpenPositions, systemCap);
    }
}
