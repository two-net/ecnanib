using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>
/// A user's trade approvals (SRS 3.11). In Confirm mode, an order the Risk Engine sized waits here for its user, who
/// approves or rejects it in the app (FR-APR-01, FR-APR-02). It expires after its timeframe's window, and an approved one
/// is checked again on fresh prices and the agent's current account before it's sent (FR-APR-03). Every call takes the
/// user, so another user's approval behaves like one that doesn't exist (NFR-SEC-04). See Approvals in
/// docs/architecture.md.
/// </summary>
public interface IApprovals
{
    /// <summary>The most decided approvals <see cref="ListAsync"/> returns.</summary>
    const int RecentCount = 50;

    /// <summary>The user's pending approvals, oldest first, then their <see cref="RecentCount"/> latest decided ones, newest first.</summary>
    Task<ApprovalList> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task<ApprovalRecord?> FindAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Approves a pending approval, which the Risk Engine then checks again on fresh prices and the agent's account as it
    /// is now. If it still passes, what it sized now is sent; if not, the approval is rejected automatically with the
    /// rules that failed (FR-APR-03). It returns once execution has settled.
    /// </summary>
    Task<ApprovalResult> ApproveAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>Rejects a pending approval, so nothing is sent.</summary>
    Task<ApprovalResult> RejectAsync(Guid userId, Guid id, CancellationToken cancellationToken);
}

/// <param name="Pending">Oldest first.</param>
/// <param name="Decided">Newest first.</param>
public sealed record ApprovalList(IReadOnlyList<ApprovalRecord> Pending, IReadOnlyList<ApprovalRecord> Decided);

/// <summary>An approval as it's stored (SRS 6.1's Approval).</summary>
/// <param name="CycleId">The decision cycle that proposed it.</param>
/// <param name="Proposal">What the cycle proposed, and what the Risk Engine sized then.</param>
/// <param name="DecidedByUser">The user decided it. False when the system did, or it isn't decided.</param>
/// <param name="Revalidation">The Risk Engine's second look, once it was approved (FR-APR-03).</param>
/// <param name="Execution">What execution did with it, once it was sent.</param>
public sealed record ApprovalRecord(
    Guid Id,
    Guid AgentId,
    Guid CycleId,
    ApprovalStatus Status,
    ApprovalEnd? End,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt,
    bool DecidedByUser,
    ApprovalProposal Proposal,
    ApprovalRevalidation? Revalidation,
    ExecutionResult? Execution)
{
    public bool IsPending => Status == ApprovalStatus.Pending;
}

/// <summary>
/// FR-APR-01's view of an approval: what the cycle proposed and what the Risk Engine sized from it. The rationale,
/// verdict and concerns are what models wrote, so they're only ever shown as text.
/// </summary>
/// <param name="ReferencePrice">For a market entry, the price it would have bought or sold at: the ask to buy and the bid to sell.</param>
/// <param name="Quantity">In the base asset.</param>
/// <param name="Notional">In the quote asset, as are every price and <paramref name="MoneyAtRisk"/>.</param>
/// <param name="Verdict">The Risk Critic's, which approved or modified the proposal.</param>
public sealed record ApprovalProposal(
    Market Market,
    TradingEnvironment Environment,
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    Timeframe Timeframe,
    IntentAction Action,
    OrderSide? Side,
    EntryType? EntryType,
    decimal? Quantity,
    decimal? LimitPrice,
    decimal? ReferencePrice,
    decimal? StopPrice,
    decimal? TargetPrice,
    decimal? Notional,
    decimal? MoneyAtRisk,
    decimal? RewardToRisk,
    int? Leverage,
    string StrategistRationale,
    VerdictKind Verdict,
    string CriticRationale,
    IReadOnlyList<string> Concerns)
{
    /// <summary>Leaves out what the models wrote (FR-LOG-07).</summary>
    public override string ToString() => $"{nameof(ApprovalProposal)} {Action} {Symbol}";
}

/// <summary>The Risk Engine's second look at an approved intent (FR-APR-03).</summary>
/// <param name="Failures">The rules that failed, in order. Empty when it passed.</param>
/// <param name="Quantity">What it sized now, which execution sent, when it passed.</param>
/// <param name="MoneyAtRisk">The same, for an entry.</param>
public sealed record ApprovalRevalidation(
    RiskVerdict Verdict,
    IReadOnlyList<RuleResult> Failures,
    decimal? Quantity,
    decimal? MoneyAtRisk,
    DateTimeOffset EvaluatedAt)
{
    public bool Passed => Verdict == RiskVerdict.Pass;
}

/// <summary>Where an approval is. Stored by name, so a value is never renamed.</summary>
public enum ApprovalStatus
{
    /// <summary>Waiting for its user, until it expires.</summary>
    Pending,

    /// <summary>Approved, and being checked again and sent.</summary>
    Approving,

    /// <summary>Approved, checked again, sent, and it took effect. <see cref="ApprovalRecord.Execution"/> says how.</summary>
    Executed,

    /// <summary>
    /// Approved, but the Risk Engine's second look failed, so it was rejected automatically and nothing was sent
    /// (FR-APR-03). <see cref="ApprovalRecord.Revalidation"/> has the rules that failed.
    /// </summary>
    Invalid,

    /// <summary>Approved, but it couldn't go: <see cref="ApprovalRecord.End"/> says why.</summary>
    Failed,

    /// <summary>Its user rejected it, so nothing was sent.</summary>
    Rejected,

    /// <summary>It can no longer be approved: <see cref="ApprovalRecord.End"/> says why.</summary>
    Expired,

    /// <summary>
    /// The application stopped while it was being sent. Whatever was sent is in the agent's orders, which
    /// reconciliation keeps in line with Binance.
    /// </summary>
    Interrupted,
}

/// <summary>Why an approval expired or failed. Stored by name, so a value is never renamed.</summary>
public enum ApprovalEnd
{
    /// <summary>Its window ended before its user decided (FR-APR-03).</summary>
    WindowEnded,

    /// <summary>Its agent was paused, stopped or put in Error, so it can't trade.</summary>
    AgentNotActive,

    /// <summary>A later cycle of the agent's proposed something newer, which replaced it.</summary>
    Superseded,

    /// <summary>The agent's market, environment, pair or timeframe changed since, so what it proposed no longer applies.</summary>
    AgentChanged,

    /// <summary>Binance couldn't give the pair, its candles or its prices, so it couldn't be checked again.</summary>
    MarketData,

    /// <summary>The agent's account couldn't be read, so it couldn't be checked again.</summary>
    Account,

    /// <summary>It passed, but Binance rejected it, couldn't be reached, or the account wasn't ready. The execution says which.</summary>
    Execution,

    /// <summary>The application began shutting down before it was sent, so nothing was.</summary>
    ShuttingDown,

    /// <summary>Something went wrong that isn't expected, which the log has.</summary>
    Unexpected,
}

/// <summary>What an approve or reject did.</summary>
/// <param name="Approval">The approval as it is now. Null for <see cref="ApprovalOutcome.NotFound"/>.</param>
public sealed record ApprovalResult(ApprovalOutcome Outcome, ApprovalRecord? Approval)
{
    public static ApprovalResult NotFound { get; } = new(ApprovalOutcome.NotFound, null);
}

public enum ApprovalOutcome
{
    /// <summary>It was approved or rejected as asked. The approval's status says how it ended.</summary>
    Done,

    /// <summary>There's no such approval for this user.</summary>
    NotFound,

    /// <summary>It was no longer pending: decided already, or expired. The approval's status says which.</summary>
    NotPending,

    /// <summary>Another approval of the same agent's is still being sent, so this one waits until it's done.</summary>
    Busy,
}

/// <summary>
/// The field names in an approval's audit summary (FR-AUD-02), which the Web UI gives text to. Records store them, so
/// never rename one.
/// </summary>
public static class ApprovalAuditFields
{
    /// <summary>An <see cref="IntentAction"/> name.</summary>
    public const string Action = "TradeAction";

    /// <summary>The pair, such as <c>BTCUSDT</c>. The same name as an agent's pair, which shares its text.</summary>
    public const string Symbol = "Symbol";

    /// <summary>An <see cref="ApprovalStatus"/> name, before and after.</summary>
    public const string Status = "ApprovalStatus";
}
