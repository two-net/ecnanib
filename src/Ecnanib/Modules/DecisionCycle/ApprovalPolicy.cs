using Ecnanib.Modules.Agents.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.DecisionCycle;

/// <summary>Which sized orders wait for their user (FR-APR-01, FR-APR-04), and what an execution's result means for the agent.</summary>
internal static class ApprovalPolicy
{
    /// <summary>
    /// A Confirm agent's order waits for approval, unless it reduces risk and the agent lets those through (FR-APR-04,
    /// on by default). An Autonomous agent's never waits.
    /// </summary>
    public static bool NeedsApproval(AgentSettings settings, IntentAction action) =>
        settings.ExecutionMode != ExecutionMode.Autonomous && !(settings.AutoExecuteRiskReducing && IsRiskReducing(action));

    /// <summary>
    /// Tightening the stop, which the Risk Engine allows only toward less risk (FR-RISK-06), selling or closing all or
    /// part of the position, and cancelling an unfilled entry. Moving a target and every entry don't.
    /// </summary>
    public static bool IsRiskReducing(IntentAction action) =>
        action is IntentAction.AdjustStop or IntentAction.Sell or IntentAction.Close or IntentAction.CancelPending;

    /// <summary>
    /// A Futures account only its user can fix: Hedge Mode, Multi-Assets Mode or trading turned off (FR-RISK-09). The
    /// agent goes to Error at once, because every later cycle would spend AI on an entry that can't be sent.
    /// </summary>
    public static bool AccountNeedsItsUser(ExecutionResult result) =>
        result.Reason == ExecutionReason.FuturesSetup && result.FuturesSetup is FuturesSetupOutcome.HedgeMode
            or FuturesSetupOutcome.MultiAssetsMode or FuturesSetupOutcome.TradingDisabled;
}
