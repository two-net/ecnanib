namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// Closes out an agent the kill switch stopped (FR-AGT-08): its open orders cancelled, and its positions closed at
/// market. Execution owns orders and positions, and Agents may not depend on it, so Agents declares this port and
/// Execution implements it (see Inversions in docs/architecture.md). <see cref="IAgents"/> resolves it lazily,
/// because its implementation uses <see cref="IAgents"/> itself.
/// </summary>
public interface IAgentLiquidation
{
    /// <summary>
    /// Cancels the agent's open orders and closes its positions at market, then returns once nothing is urgent. Call it
    /// only after the agent is Stopped with <see cref="AgentStatusReason.KillSwitch"/>, which is what tells execution
    /// to close rather than protect: reconciliation keeps closing on that alone, so whatever this call doesn't finish,
    /// such as a close Binance couldn't take yet, or a restart, is finished later. Only the agent's own orders are
    /// touched (FR-AGT-09).
    /// </summary>
    Task<LiquidationOutcome> CloseAllAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}

/// <summary>How far closing an agent got.</summary>
public enum LiquidationOutcome
{
    /// <summary>It had no open orders or positions.</summary>
    NothingOpen,

    /// <summary>Every open order is cancelled and every position closed.</summary>
    Closed,

    /// <summary>Under way: a close or cancel is still being settled or tried. Execution finishes it on its own.</summary>
    Closing,

    /// <summary>
    /// Binance couldn't confirm the agent's orders, or its key can't be used, so nothing was sent. Its stops stay on
    /// Binance, and reconciliation tries again at its interval.
    /// </summary>
    Unconfirmed,
}
