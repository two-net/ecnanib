namespace Ecnanib.Agents;

/// <summary>
/// The state of an agent (SRS 3.3). A member is stored by its name, so renaming one changes
/// stored data and needs a migration. See docs/agents.md.
/// </summary>
/// <remarks>
/// An agent is created in <see cref="Draft"/>. What moves an agent to another state is not built
/// yet: the states are here because the rules for an agent's settings are stated in them
/// (FR-AGT-8, FR-AGT-9).
/// </remarks>
public enum AgentState
{
    /// <summary>The agent is created and not yet started. It runs no cycle and has no position.</summary>
    Draft,

    /// <summary>The user started or resumed the agent. Its cycles run.</summary>
    Running,

    /// <summary>The user paused the agent. An open position stays open with its protective orders.</summary>
    Paused,

    /// <summary>A loss limit of the Risk Guard was reached (FR-RSK-8).</summary>
    Halted,

    /// <summary>A key is invalid, a model is unavailable, an exchange setting conflicts, or calls fail repeatedly.</summary>
    AttentionRequired,

    /// <summary>The user stopped the agent. An open position was closed or kept, as the user chose.</summary>
    Stopped,
}

/// <summary>
/// Why an agent is in its state. A member is stored by its name. What changes the state of an
/// agent adds the members of its causes when it is built (FR-AGT-10).
/// </summary>
public enum AgentStateCause
{
    /// <summary>The user created the agent, as a new one or as a copy of another.</summary>
    Created,
}

/// <summary>What the state of an agent means for the rules about its settings.</summary>
public static class AgentStates
{
    /// <summary>
    /// Whether an agent in a state is active: in any state other than Draft or Stopped. A user
    /// has at most one active agent for each Binance credential, market, and symbol (FR-AGT-9),
    /// and an account is deleted only while none of its agents is active (FR-ACC-9).
    /// </summary>
    public static bool IsActive(AgentState state) => state is not (AgentState.Draft or AgentState.Stopped);

    /// <summary>
    /// Whether the mode, market, symbol, and timeframe of an agent can be changed, and whether
    /// the agent can be deleted: while it is in Draft, or is Stopped with no open position or
    /// orders (FR-AGT-8).
    /// </summary>
    /// <param name="state">The state of the agent.</param>
    /// <param name="holdsPositionOrOrders">Whether the agent has an open position or open orders on the exchange.</param>
    public static bool StructureCanChange(AgentState state, bool holdsPositionOrOrders) =>
        state == AgentState.Draft || (state == AgentState.Stopped && !holdsPositionOrOrders);
}
