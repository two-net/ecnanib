namespace Ecnanib.Agents;

/// <summary>
/// The state of an agent (SRS 3.3). A member is stored by its name, so renaming one changes
/// stored data and needs a migration. See docs/agents.md.
/// </summary>
/// <remarks>
/// An agent is created in <see cref="Draft"/>. Its user starts, pauses, resumes, and stops it
/// (FR-AGT-1), and the service moves it to <see cref="AttentionRequired"/> when a key it runs
/// with goes, or a model of its team does. Which control leads where is in <see cref="AgentStates.After"/>. Nothing moves an
/// agent to <see cref="Halted"/> yet: what watches the loss limits does, when it is built.
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
/// Why an agent is in its state (FR-AGT-10). A member is stored by its name, so renaming one
/// changes stored data and needs a migration. What changes the state of an agent adds the
/// members of its causes when it is built.
/// </summary>
public enum AgentStateCause
{
    /// <summary>The user created the agent, as a new one or as a copy of another.</summary>
    Created,

    /// <summary>The user started the agent, which was a draft or was stopped.</summary>
    UserStarted,

    /// <summary>The user paused the agent.</summary>
    UserPaused,

    /// <summary>The user resumed the agent, which was paused, was halted, or required attention.</summary>
    UserResumed,

    /// <summary>The user stopped the agent, which had no open position.</summary>
    UserStopped,

    /// <summary>The user stopped the agent, and chose to close its open position at market (FR-AGT-11).</summary>
    UserStoppedClosingPosition,

    /// <summary>The user stopped the agent, and chose to keep its open position with its protective orders (FR-AGT-11).</summary>
    UserStoppedKeepingPosition,

    /// <summary>The Binance credential of the agent's environment was deleted while the agent ran (FR-KEY-10).</summary>
    ExchangeKeyDeleted,

    /// <summary>Binance refused the credential of the agent's environment while the agent ran (FR-KEY-10).</summary>
    ExchangeKeyInvalid,

    /// <summary>The key of an AI provider that a role of the agent's team uses was deleted while the agent ran (FR-KEY-10).</summary>
    AiKeyDeleted,

    /// <summary>An AI provider refused the key that a role of the agent's team uses while the agent ran (FR-KEY-10).</summary>
    AiKeyInvalid,

    /// <summary>The provider of a model that a role of the agent's team was given no longer offers it, while the agent ran (FR-CAT-8).</summary>
    ModelUnavailable,
}

/// <summary>What a user does to the state of an agent (FR-AGT-1).</summary>
public enum AgentControl
{
    /// <summary>Starts an agent that is a draft, or is stopped.</summary>
    Start,

    /// <summary>Pauses an agent that runs.</summary>
    Pause,

    /// <summary>Resumes an agent that is paused, is halted, or requires attention.</summary>
    Resume,

    /// <summary>Stops an agent that was started.</summary>
    Stop,
}

/// <summary>What the state of an agent means: for its cycles, for its settings, and for what its user can do to it (SRS 3.3).</summary>
public static class AgentStates
{
    /// <summary>
    /// Whether an agent in a state is active: in any state other than Draft or Stopped. A user
    /// has at most one active agent for each Binance credential, market, and symbol (FR-AGT-9),
    /// and an account is deleted only while none of its agents is active (FR-ACC-9).
    /// </summary>
    public static bool IsActive(AgentState state) => state is not (AgentState.Draft or AgentState.Stopped);

    /// <summary>
    /// Whether the cycles of an agent in a state run: only while it is Running. What runs a
    /// cycle asks this when the candle closes.
    /// </summary>
    public static bool RunsCycles(AgentState state) => state == AgentState.Running;

    /// <summary>
    /// Whether an agent in a state may open a new position: only while it is Running. In every
    /// other state an open position is left as the state says, and none is opened.
    /// </summary>
    public static bool TakesNewEntries(AgentState state) => state == AgentState.Running;

    /// <summary>
    /// Whether an agent in a state needs the Binance credential of its environment now, and the
    /// keys and the models of its team: while it is Running. Such an agent moves to Attention
    /// required when one of them is deleted, is refused, or is gone (FR-KEY-10, FR-CAT-8). An
    /// agent that does not run is asked about them when it is started or resumed, and stays as
    /// its user left it until then.
    /// </summary>
    public static bool NeedsItsKey(AgentState state) => state == AgentState.Running;

    /// <summary>
    /// Whether the mode, market, symbol, and timeframe of an agent can be changed, and whether
    /// the agent can be deleted: while it is in Draft, or is Stopped with no open position or
    /// orders (FR-AGT-8).
    /// </summary>
    /// <param name="state">The state of the agent.</param>
    /// <param name="holdsPositionOrOrders">Whether the agent has an open position or open orders on the exchange.</param>
    public static bool StructureCanChange(AgentState state, bool holdsPositionOrOrders) =>
        state == AgentState.Draft || (state == AgentState.Stopped && !holdsPositionOrOrders);

    /// <summary>
    /// The state that a control leads to from a state, or null when the control is not offered
    /// in it. This is every move that a user makes (SRS 3.3):
    /// a draft and a stopped agent are started; a running one is paused; a paused one, a halted
    /// one, and one that requires attention are resumed; and every agent that was started and is
    /// not stopped is stopped.
    /// </summary>
    public static AgentState? After(AgentState state, AgentControl control) => (control, state) switch
    {
        (AgentControl.Start, AgentState.Draft or AgentState.Stopped) => AgentState.Running,
        (AgentControl.Pause, AgentState.Running) => AgentState.Paused,
        (AgentControl.Resume, AgentState.Paused or AgentState.Halted or AgentState.AttentionRequired) => AgentState.Running,
        (AgentControl.Stop, AgentState.Running or AgentState.Paused or AgentState.Halted or AgentState.AttentionRequired) =>
            AgentState.Stopped,
        _ => null,
    };

    /// <summary>The state that a control leaves an agent in, wherever it is offered.</summary>
    public static AgentState LeadsTo(AgentControl control) => control switch
    {
        AgentControl.Start or AgentControl.Resume => AgentState.Running,
        AgentControl.Pause => AgentState.Paused,
        AgentControl.Stop => AgentState.Stopped,
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, "The control leads to no state."),
    };

    /// <summary>The controls that are offered for an agent in a state, in the order of <see cref="AgentControl"/>.</summary>
    public static IReadOnlyList<AgentControl> Offered(AgentState state) =>
        [.. Enum.GetValues<AgentControl>().Where(control => After(state, control) is not null)];
}
