using Ecnanib.Kernel;

namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>
/// Where a decision cycle is, for the council view (FR-ROLE-08, FR-ANI-01): each role's state and summary, and the
/// outcome once it's decided. <see cref="ICycleObserver"/> hears every change, and <see cref="IDecisionTraces"/> reads
/// the latest. See Decision cycle in docs/architecture.md.
/// </summary>
/// <param name="Outcome">Null while the cycle runs.</param>
/// <param name="Failure">Why it ended without a decision, or null.</param>
public sealed record CycleProgress(
    Guid CycleId,
    Guid UserId,
    Guid AgentId,
    DateTimeOffset CandleClose,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    CycleOutcome? Outcome,
    CycleFailure? Failure,
    IReadOnlyList<RoleProgress> Roles);

/// <summary>One role in a cycle.</summary>
/// <param name="Provider">The provider of the model that answered, or the last one tried. Null before it runs.</param>
/// <param name="UsedFallback">The role's fallback model answered, or was the last one tried (FR-ROLE-06).</param>
/// <param name="Summary">
/// What the role concluded, at most 600 characters. A model wrote it, so it's only ever shown as text.
/// </param>
public sealed record RoleProgress(AiRole Role, RoleState State, string? Provider, string? ModelId, bool UsedFallback, string? Summary)
{
    /// <summary>Leaves the summary out, because a model wrote it (FR-LOG-07).</summary>
    public override string ToString() => $"{nameof(RoleProgress)} {Role} {State}";
}

/// <summary>FR-ROLE-08's states. Stored by name, so a value is never renamed.</summary>
public enum RoleState
{
    Queued,
    Running,
    Done,
    Failed,

    /// <summary>Never asked: the cycle ended before it, or an optional role failed and the cycle went on without it.</summary>
    Skipped,
}

/// <summary>How a cycle ended. Stored by name, so a value is never renamed.</summary>
public enum CycleOutcome
{
    /// <summary>No new entry: the Strategist held, or a required role failed (FR-ROLE-06), which <see cref="CycleFailure"/> says.</summary>
    Hold,

    /// <summary>The Risk Critic rejected the proposal.</summary>
    RejectedByCritic,

    /// <summary>The Risk Engine failed the order intent (FR-RISK-01). Its rules are in the trace.</summary>
    BlockedByRiskEngine,

    /// <summary>The intent passed, but it was an entry and the cycle's deadline had passed (FR-CYC-06).</summary>
    PastDeadline,

    /// <summary>
    /// The Risk Engine sized the intent, and it waits for the user's approval in Confirm mode (#38). An Autonomous
    /// agent's cycle goes on to <see cref="Executed"/> or <see cref="ExecutionFailed"/>.
    /// </summary>
    ReadyToExecute,

    /// <summary>A loss limit was reached, so the agent was paused (FR-RISK-08).</summary>
    PausedForLossLimit,

    /// <summary>
    /// The agent's AI budget for the day or month had run out, so the cycle asked no role and made no entry:
    /// protective-only mode (FR-CYC-07). The agent stays Active, and its positions and protective orders stay.
    /// </summary>
    OverBudget,

    /// <summary>The cycle couldn't decide: <see cref="CycleFailure"/> says why.</summary>
    Failed,

    /// <summary>The application stopped before the cycle finished.</summary>
    Interrupted,

    /// <summary>
    /// The order went to Binance and took effect: an entry placed, filled and protected, or closed when its stop
    /// couldn't be placed, or an unfilled entry cancelled (FR-EXE-01, FR-EXE-02). The trace's execution says which.
    /// </summary>
    Executed,

    /// <summary>
    /// Nothing took effect: Binance rejected the order, couldn't be reached, or the Futures symbol couldn't be readied.
    /// The trace's execution says why, and <see cref="CycleFailure.Execution"/> marks the ones that are errors.
    /// </summary>
    ExecutionFailed,
}

/// <summary>
/// Why a cycle ended without a decision. Cycles that do, too many in a row, put the agent in Error. Stored by name,
/// so a value is never renamed.
/// </summary>
public enum CycleFailure
{
    /// <summary>Binance couldn't give the pair, its candles or its prices, or the latest candle was missing.</summary>
    MarketData,

    /// <summary>The agent's account couldn't be read, or it has no exchange key.</summary>
    Account,

    /// <summary>A required role failed on its model and its fallback (FR-ROLE-06).</summary>
    RoleFailed,

    /// <summary>Something went wrong that the cycle doesn't expect, which the log has.</summary>
    Unexpected,

    /// <summary>The order passed the Risk Engine, but Binance rejected it, couldn't be reached, or the account wasn't ready.</summary>
    Execution,
}
