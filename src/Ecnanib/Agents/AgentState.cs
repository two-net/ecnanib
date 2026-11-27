namespace Ecnanib.Agents;

/// <summary>
/// The state of an agent (SRS 3.3). A member is stored by its name, so renaming one changes
/// stored data and needs a migration. See docs/agents.md.
/// </summary>
/// <remarks>
/// An agent is created in <see cref="Draft"/>. Its user starts, pauses, resumes, and stops it
/// (FR-AGT-1), and the service moves it to <see cref="AttentionRequired"/> when a key it runs
/// with goes, or a model of its team does. Which control leads where is in <see cref="AgentStates.After"/>. What watches
/// the loss limits moves an agent that runs to <see cref="Halted"/> when one is reached, and the
/// service resumes an agent that its daily loss limit halted when the next UTC day begins
/// (FR-RSK-8): see <see cref="IAgentHalts"/>.
/// </remarks>
public enum AgentState
{
    /// <summary>The agent is created and not yet started. It runs no cycle and has no position.</summary>
    Draft,

    /// <summary>The user started or resumed the agent. Its cycles run.</summary>
    Running,

    /// <summary>The user paused the agent. An open position stays open with its protective orders.</summary>
    Paused,

    /// <summary>A loss limit of the Risk Guard was reached, and the service closes the position of the agent (FR-RSK-8).</summary>
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

    /// <summary>
    /// The position of the agent is open with no stop-loss on the exchange, and could not be
    /// closed at market either, while the agent ran (FR-RSK-7).
    /// </summary>
    PositionUnprotected,

    /// <summary>
    /// The account of the agent's user at the exchange conflicts with how the agent trades,
    /// while the agent ran: on the Futures market it is in a mode that is not one-way mode with
    /// one asset, or it holds a position in the symbol that the agent did not open (FR-BIN-F2).
    /// </summary>
    ExchangeSettingConflict,

    /// <summary>
    /// The loss of the agent in the UTC day reached its daily loss limit while the agent ran:
    /// the service closes its position, and halts it until the next UTC day (FR-RSK-8).
    /// </summary>
    DailyLossLimitReached,

    /// <summary>
    /// The equity of the agent fell from its peak by its drawdown limit while the agent ran:
    /// the service closes its position, and halts it until its user resumes it (FR-RSK-8).
    /// </summary>
    DrawdownLimitReached,

    /// <summary>
    /// The UTC day in which the daily loss limit of the agent halted it was over, and the
    /// service resumed the agent (FR-RSK-8).
    /// </summary>
    DailyLossHaltEnded,

    /// <summary>
    /// An operator disabled the account of the agent's user while the agent ran (FR-OPS-4). The
    /// agent waits for its user, who resumes it once the account is enabled again.
    /// </summary>
    AccountDisabled,

    /// <summary>
    /// The AI provider of a role of the agent's team kept refusing the calls with the key of the
    /// agent's user while the agent ran: the calls with the key were paused and made again
    /// several times in a row, and the provider took none (NFR-REL-4). A provider that does not
    /// answer anyone is no cause: its calls are paused and made again, and the agent goes on.
    /// </summary>
    CallsFailRepeatedly,
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
    /// Whether an agent in a state acts for its user now, and so moves to Attention required when
    /// an operator disables the account of its user: while it is Running (FR-OPS-4). An agent
    /// that a loss limit halted stays halted, so that the service goes on closing its position,
    /// and its halt does not end while the account is disabled. Every other agent stays as its
    /// user left it: none of them runs, and a disabled account starts and resumes none.
    /// </summary>
    public static bool StopsWithItsAccount(AgentState state) => state == AgentState.Running;

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

    /// <summary>
    /// When the halt of an agent ends by itself, or null when it does not: an agent that its
    /// daily loss limit halted is halted until the next UTC day (FR-RSK-8), which begins at
    /// midnight, UTC, after the moment that it was halted at. Until then its user does not
    /// resume it either. An agent that its drawdown limit halted is halted until its user
    /// resumes it, at any time, and an agent in another state is not halted.
    /// </summary>
    /// <param name="state">The state of the agent.</param>
    /// <param name="cause">Why it is in that state.</param>
    /// <param name="since">Since when.</param>
    public static DateTimeOffset? HaltEnds(AgentState state, AgentStateCause cause, DateTimeOffset since) =>
        state == AgentState.Halted && cause == AgentStateCause.DailyLossLimitReached ? DayAfter(since) : null;

    /// <summary>
    /// When the UTC day after a moment begins: midnight, UTC. An agent that its daily loss limit
    /// halted at that moment does not run before it, whatever its user does: also not when they
    /// stop the agent and start it again (FR-RSK-8).
    /// </summary>
    /// <param name="moment">The moment, with whatever offset it is written.</param>
    public static DateTimeOffset DayAfter(DateTimeOffset moment) => new DateTimeOffset(moment.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);

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
