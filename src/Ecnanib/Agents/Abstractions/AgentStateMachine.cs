using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Abstractions;

/// <summary>User actions on an agent (FR-AGT-04).</summary>
public enum AgentAction
{
    Start,
    Pause,
    Resume,
    Stop,
    Delete,
    Clone,
}

/// <summary>Editable parts of an agent, grouped by the state rule that applies to them (FR-AGT-07).</summary>
public enum AgentField
{
    Name,
    Discretionary,
    MarketType,
    Symbol,
    Timeframe,
    Mode,
    Strategy,
    ExchangeKey,
    Leverage,
    MarginType,
    Parameters,
    Council,
    RiskLimits,
}

/// <summary>
/// The Appendix E lifecycle as pure rules: which user action a state allows, and which fields may be edited in it
/// (FR-AGT-04, FR-AGT-07). The runtime's own transitions (daily-loss pause, key failure, unrecoverable failure,
/// auto-resume) are not user actions and are not listed here.
/// </summary>
public static class AgentStateMachine
{
    /// <summary>Draft, Stopped and Error may be started; Error after the user fixed the cause.</summary>
    public static bool CanStart(AgentState state) => state is AgentState.Draft or AgentState.Stopped or AgentState.Error;

    public static bool CanPause(AgentState state) => state == AgentState.Running;

    public static bool CanResume(AgentState state) => state == AgentState.Paused;

    public static bool CanStop(AgentState state) => state is AgentState.Running or AgentState.Paused or AgentState.Error;

    public static bool CanDelete(AgentState state) => state is AgentState.Draft or AgentState.Stopped;

    /// <summary>Running and Paused agents are evaluated at every candle close (FR-AGT-09).</summary>
    public static bool IsEvaluated(AgentState state) => state is AgentState.Running or AgentState.Paused;

    public static bool Allows(AgentState state, AgentAction action) => action switch
    {
        AgentAction.Start => CanStart(state),
        AgentAction.Pause => CanPause(state),
        AgentAction.Resume => CanResume(state),
        AgentAction.Stop => CanStop(state),
        AgentAction.Delete => CanDelete(state),
        AgentAction.Clone => true,
        _ => false,
    };

    /// <summary>
    /// FR-AGT-07: timeframe, market, symbol and mode only in Draft or Stopped; strategy parameters, council and risk
    /// limits also in Paused (they take effect at the next evaluation) and in Error (fixing the cause). The exchange
    /// key may be re-pointed in Error for the same reason. A Running agent accepts a new name and nothing else:
    /// pause it to edit.
    /// </summary>
    public static bool CanEdit(AgentState state, AgentField field) => field switch
    {
        AgentField.Name => true,
        AgentField.Discretionary => state != AgentState.Running,
        AgentField.MarketType or AgentField.Symbol or AgentField.Timeframe or AgentField.Mode or AgentField.Strategy
            or AgentField.Leverage or AgentField.MarginType => state is AgentState.Draft or AgentState.Stopped,
        AgentField.ExchangeKey => state is AgentState.Draft or AgentState.Stopped or AgentState.Error,
        AgentField.Parameters or AgentField.Council or AgentField.RiskLimits =>
            state is AgentState.Draft or AgentState.Stopped or AgentState.Paused or AgentState.Error,
        _ => false,
    };
}
