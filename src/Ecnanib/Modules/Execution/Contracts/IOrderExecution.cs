using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>
/// Sends what the Risk Engine sized to Binance, once (FR-EXE-04, FR-EXE-05), and protects every fill with an
/// exchange-resting stop, or closes it at market when the stop can't be placed (FR-EXE-02, AC-02). It executes
/// entries and adds, exits, stop and target adjustments, and CANCEL_PENDING, each after reconciling the agent's orders
/// with Binance (FR-EXE-06). Only a <see cref="SizedOrder"/> is sent, which only a passing evaluation makes (AC-01). The decision cycle calls it for an
/// Autonomous agent, and approvals (#38) will, so <c>BannedApis</c> allows it only in DecisionCycle and Execution. See
/// Order execution in docs/architecture.md.
/// </summary>
public interface IOrderExecution
{
    /// <summary>
    /// Sends an entry and protects what fills, sells an exit, moves the position's stop or target, or cancels the
    /// agent's unfilled entry. It returns once what was sent has settled and anything filled is protected or closed.
    /// What's left, such as a resting limit order, is watched in the background. Pass the run's token, never
    /// <c>Stopping</c>: a trade once sent is seen through (NFR-REL-04).
    /// </summary>
    /// <exception cref="ArgumentException">The order isn't one this version executes for the request's market.</exception>
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>What to execute, and for whom.</summary>
/// <param name="CycleId">The cycle whose passing evaluation sized the order, which every order it leads to cites (AC-01).</param>
/// <param name="CandleClose">The cycle's candle close, which client order IDs are derived from (FR-EXE-04).</param>
/// <param name="CredentialId">The agent's exchange key, opened for this use only (FR-KEY-09).</param>
/// <param name="EntryDeadline">After this, no entry is sent (FR-CYC-06). It's checked right before sending. Exits and adjustments ignore it.</param>
public sealed record ExecutionRequest(
    Guid UserId,
    Guid AgentId,
    Guid CycleId,
    DateTimeOffset CandleClose,
    Guid CredentialId,
    Market Market,
    TradingEnvironment Environment,
    string Symbol,
    Timeframe Timeframe,
    SizedOrder Order,
    DateTimeOffset EntryDeadline)
{
    /// <summary>
    /// A limit entry's life in candles of <see cref="Timeframe"/>, as the Strategist proposed it (FR-EXE-01). It's
    /// brought down to <c>Agents:MaxLimitTtlCandles</c>, and a limit entry without one lives a single candle.
    /// </summary>
    public int? LimitTtlCandles { get; init; }
}

/// <summary>How an execution went, for the decision trace.</summary>
/// <param name="Reason">Why nothing was sent, for <see cref="ExecutionStatus.NotSent"/>.</param>
public sealed record ExecutionResult(ExecutionStatus Status, ExecutionReason? Reason = null)
{
    /// <summary>Why a Futures symbol couldn't be readied, for <see cref="ExecutionReason.FuturesSetup"/>.</summary>
    public FuturesSetupOutcome? FuturesSetup { get; init; }

    /// <summary>Binance's code for a rejection.</summary>
    public int? ExchangeCode { get; init; }

    /// <summary>The entry had a target that Binance refused, so only its stop rests (FR-EXE-03).</summary>
    public bool TargetNotPlaced { get; init; }

    /// <summary>The client order IDs of the orders sent, in the order they were sent.</summary>
    public IReadOnlyList<string> ClientOrderIds { get; init; } = [];

    /// <summary>Whether an order went to Binance and took effect, which is what the cycle's outcome says.</summary>
    public bool Executed => Status is ExecutionStatus.Placed or ExecutionStatus.Protected or ExecutionStatus.Closed
        or ExecutionStatus.Unprotected or ExecutionStatus.Cancelled or ExecutionStatus.Exited or ExecutionStatus.Adjusted;
}

/// <summary>Stored by name, so a value is never renamed.</summary>
public enum ExecutionStatus
{
    /// <summary>A limit entry rests on Binance unfilled. It's watched until it fills or its time-to-live ends.</summary>
    Placed,

    /// <summary>The entry filled, or partly, and an exchange-resting stop protects what filled.</summary>
    Protected,

    /// <summary>The entry filled, but its stop couldn't be placed, so what filled was closed at market (FR-EXE-02).</summary>
    Closed,

    /// <summary>
    /// The entry filled, its stop couldn't be placed, and closing it hasn't worked yet. It's kept at in the
    /// background, and logged as an Error.
    /// </summary>
    Unprotected,

    /// <summary>The unfilled entry was cancelled (CANCEL_PENDING). Anything that had filled is protected.</summary>
    Cancelled,

    /// <summary>Nothing was sent: <see cref="ExecutionResult.Reason"/> says why.</summary>
    NotSent,

    /// <summary>Binance rejected the entry, with <see cref="ExecutionResult.ExchangeCode"/>.</summary>
    Rejected,

    /// <summary>The entry's outcome was unknown, and Binance then showed it was never placed. It isn't sent again.</summary>
    NotPlaced,

    /// <summary>
    /// The entry's or the exit's outcome is still unknown, or the exit's close is still being tried. It's settled in the
    /// background, and an entry's fill is protected.
    /// </summary>
    Unsettled,

    /// <summary>The exit sold what it was sized for, or what was still held, at market.</summary>
    Exited,

    /// <summary>The position's stop or target was moved on Binance, or removed.</summary>
    Adjusted,
}

/// <summary>Why nothing was sent. Stored by name, so a value is never renamed.</summary>
public enum ExecutionReason
{
    /// <summary>The cycle's deadline passed before the entry could be sent (FR-CYC-06).</summary>
    PastDeadline,

    /// <summary>
    /// No longer given: before reconciliation (#35), an entry wasn't sent while the agent held a position. Kept for
    /// traces that recorded it.
    /// </summary>
    PositionOpen,

    /// <summary>The Futures symbol couldn't be readied (FR-RISK-09): <see cref="ExecutionResult.FuturesSetup"/> says why.</summary>
    FuturesSetup,

    /// <summary>The exchange key was deleted or stopped being valid.</summary>
    KeyUnusable,

    /// <summary>Binance couldn't be reached, is limiting requests, or has banned this server's address. Nothing was sent.</summary>
    ExchangeUnavailable,

    /// <summary>Binance doesn't take the order types the entry's protection needs on this symbol.</summary>
    OrderTypeUnsupported,

    /// <summary>There's no unfilled entry to cancel.</summary>
    NothingToCancel,

    /// <summary>
    /// No longer given: before reconciliation (#35), exits and stop or target adjustments weren't executed. Kept for
    /// traces that recorded it.
    /// </summary>
    Unsupported,

    /// <summary>The agent no longer holds a position to exit or adjust: its stop or target executed, or it was closed outside Ecnanib.</summary>
    NoPosition,

    /// <summary>
    /// Futures: Binance holds a position in the pair that isn't only the agent's, such as one the user opened. One-way
    /// Mode would net an entry against it, so none is sent.
    /// </summary>
    ForeignPosition,
}
