using Ecnanib.Platform.Persistence;

namespace Ecnanib.Agents;

/// <summary>
/// What Agents asks about what an agent holds on the exchange, before the market, symbol, or
/// timeframe of a stopped agent is changed, and before a stopped agent is deleted: both are done
/// only while it has no open position or orders (FR-AGT-8). Agents may not use the Decision
/// Engine, so it declares this interface, and whatever keeps the positions and the orders
/// implements and registers it. See "When a lower component must trigger a higher one" in
/// docs/architecture.md.
/// </summary>
/// <remarks>
/// <para>
/// Nothing implements it until positions and orders are built. Until then no agent holds any,
/// and nothing stands in the way.
/// </para>
/// <para>
/// It is asked with the context of whoever asks, inside its transaction where it has one. An
/// implementation asks for the rows of the agent it is given, and commits nothing.
/// </para>
/// </remarks>
public interface IAgentExposure
{
    /// <summary>Whether an agent has an open position or open orders.</summary>
    /// <param name="context">The context of whoever asks.</param>
    /// <param name="agentId">The agent that is asked about.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<bool> HasOpenPositionOrOrdersAsync(EcnanibDbContext context, Guid agentId, CancellationToken cancellationToken);
}
