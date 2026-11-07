using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;
using Ecnanib.Modules.Execution.Contracts;
using Ecnanib.Modules.MarketData.Contracts;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>One of an agent's cycles, for its decision timeline (FR-UI-01).</summary>
/// <param name="Environment">The cycle's own, which after a switch to Live may not be the agent's (FR-EXP-08).</param>
/// <param name="Outcome">How it ended, or null while it's running.</param>
/// <param name="Action">What the Strategist proposed, or null when it didn't answer, or wasn't asked.</param>
public sealed record CycleSummary(
    Guid CycleId,
    DateTimeOffset CandleClose,
    TradingEnvironment Environment,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    CycleOutcome? Outcome,
    CycleFailure? Failure,
    TradeAction? Action,
    TraceUsage Usage);

/// <summary>
/// What a cycle's AI calls used, summed from the calls themselves. A cycle's own totals are written when it finishes,
/// so they're 0 while it runs and for one a crash interrupted, and the calls are saved as each role finishes.
/// </summary>
/// <param name="Cost">In US dollars, every priced call's, or null when none had a price.</param>
/// <param name="UnpricedTokens">Input and output tokens of calls to a model without a price, which have no cost.</param>
/// <param name="AiTime">The calls' latencies added up. Roles that run at once overlap, so it can be more than the cycle took.</param>
public sealed record TraceUsage(long InputTokens, long OutputTokens, decimal? Cost, long UnpricedTokens, TimeSpan AiTime)
{
    public static TraceUsage None { get; } = new(0, 0, null, 0, TimeSpan.Zero);
}

/// <summary>
/// Everything behind one cycle, for the decision trace viewer (FR-UI-04): what it saw, each role's prompt and answer,
/// the Risk Engine's rules, the approval and what execution did. It holds the user's prompts and models' answers, so
/// only its user reads it, and never an administrator (FR-ADM-01). Free text is masked like the logs before it's
/// returned. The orders are Execution's, through <c>ICycleOrders</c>.
/// </summary>
/// <param name="Environment">The cycle's own (FR-EXP-08).</param>
/// <param name="Deadline">After this, the cycle executes no new entry (FR-CYC-06).</param>
/// <param name="Snapshot">Null when the cycle ended before it collected the market and the account.</param>
/// <param name="LossLimits">The loss limits' rules, checked before anything else. Empty for older traces.</param>
/// <param name="Budget">The AI budget check, when the agent has a limit (FR-CYC-07).</param>
/// <param name="Roles">In role order, each with every call it made.</param>
/// <param name="Evaluation">The Risk Engine's, when the council proposed a trade the Risk Critic didn't reject.</param>
/// <param name="Approval">A Confirm agent's, when the cycle ended awaiting one.</param>
/// <param name="Execution">What execution did, when the cycle sent the order itself. An approval's is on the approval.</param>
public sealed record CycleTrace(
    Guid CycleId,
    Guid AgentId,
    Market Market,
    TradingEnvironment Environment,
    string Symbol,
    Timeframe Timeframe,
    DateTimeOffset CandleClose,
    bool CatchUp,
    DateTimeOffset Deadline,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    CycleOutcome? Outcome,
    CycleFailure? Failure,
    InputSnapshot? Snapshot,
    IReadOnlyList<RuleResult> LossLimits,
    BudgetCheck? Budget,
    IReadOnlyList<RoleTrace> Roles,
    EvaluationTrace? Evaluation,
    ApprovalTrace? Approval,
    ExecutionResult? Execution,
    TraceUsage Usage)
{
    /// <summary>Leaves out the prompts and answers (FR-LOG-07).</summary>
    public override string ToString() => $"{nameof(CycleTrace)} {CycleId} {Outcome}";
}

/// <summary>
/// A summary of what a cycle saw (FR-UI-04): the latest candle of each series, their indicators, the prices, the
/// account and the limits that held. The candles each role saw are in its prompt.
/// </summary>
/// <param name="CandleClose">The close of the series' last candle.</param>
/// <param name="AvailableQuote">Spot: the quote asset's free balance. Futures: the available margin balance.</param>
/// <param name="BaseHeld">Spot only: all of the base asset, free or locked. Null on Futures.</param>
/// <param name="Position">The agent's own open position, as Execution's records had it after reconciling.</param>
/// <param name="Effective">The agent's risk settings with the caps applied (FR-RISK-11).</param>
public sealed record InputSnapshot(
    string Symbol,
    DateTimeOffset CandleClose,
    Candle? LastCandle,
    Candle? LastHigherTimeframeCandle,
    IndicatorSet Indicators,
    IndicatorSet HigherTimeframeIndicators,
    Quote Quote,
    FuturesData? Futures,
    decimal AvailableQuote,
    decimal? BaseHeld,
    OpenPosition? Position,
    bool HasPendingEntry,
    decimal CapitalAllocation,
    RiskSettings Effective);

/// <summary>One role in a cycle, with every call it made, billed or not.</summary>
/// <param name="Summary">What the role concluded. A model wrote it, so it's only ever shown as text.</param>
public sealed record RoleTrace(
    AiRole Role,
    RoleState State,
    string? Provider,
    string? ModelId,
    bool UsedFallback,
    string? Summary,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<CallTrace> Calls)
{
    /// <summary>Leaves out the summary and the calls' text (FR-LOG-07).</summary>
    public override string ToString() => $"{nameof(RoleTrace)} {Role} {State}";
}

/// <summary>
/// One call to a model, or one that couldn't be sent (SRS 6.1's AiCall). Every text in it is the user's or a
/// model's, so it's only ever shown as text.
/// </summary>
/// <param name="Attempt">1, or 2 for the retry an answer that broke its contract gets (FR-CYC-05). The retry also sent the first answer and its violations, which are on the call before it.</param>
/// <param name="ProviderMessage">The provider's own message on a failure, which can quote the prompt.</param>
/// <param name="Instructions">Prompt layers 1 to 4: system, output contract, role, market and timeframe.</param>
/// <param name="Message">Prompt layers 5 and 6: the custom instructions, then the runtime context.</param>
/// <param name="Answer">What the model wrote, as it wrote it.</param>
/// <param name="Output">The checked output, as indented JSON in its contract's names, when the answer conformed.</param>
/// <param name="Violations">What broke the contract, when it did.</param>
/// <param name="Templates">The prompt template versions used, in layer order (FR-PRM-03).</param>
/// <param name="Cost">In US dollars, or null for a model without a price.</param>
/// <param name="ProviderAttempts">How many times the adapter sent it, its own retries included (EXT-AI-03).</param>
/// <param name="Truncated">The answer reached the output limit.</param>
public sealed record CallTrace(
    string Provider,
    string ModelId,
    bool Fallback,
    int Attempt,
    CouncilCallStatus Status,
    CouncilFailure? Failure,
    string? ProviderMessage,
    string? Instructions,
    string? Message,
    string? Answer,
    string? Output,
    IReadOnlyList<ContractViolation> Violations,
    IReadOnlyList<PromptTemplateVersion> Templates,
    long InputTokens,
    long OutputTokens,
    decimal? Cost,
    TimeSpan Latency,
    int ProviderAttempts,
    bool Truncated,
    DateTimeOffset StartedAt)
{
    /// <summary>Leaves out the prompt, the answer and the provider's message (FR-LOG-07).</summary>
    public override string ToString() => $"{nameof(CallTrace)} {Provider}/{ModelId} {Attempt} {Status}";
}

/// <summary>The Risk Engine's evaluation of the cycle's intent: every rule's inputs, outcome and reason (FR-RISK-10).</summary>
/// <param name="Effective">The agent's settings with the caps applied, which is what the rules used.</param>
/// <param name="Sized">What it allowed to be sent, when it passed.</param>
/// <param name="Breach">A loss limit the agent reached, which paused it (FR-RISK-08).</param>
public sealed record EvaluationTrace(
    RiskVerdict Verdict,
    DateTimeOffset EvaluatedAt,
    OrderIntent Intent,
    RiskCaps Caps,
    RiskSettings Effective,
    IReadOnlyList<RuleResult> Rules,
    TracedOrder? Sized,
    LossLimitBreach? Breach);

/// <summary>
/// A <see cref="SizedOrder"/> as the trace keeps it. Only the Risk Engine makes a <see cref="SizedOrder"/>, so the trace
/// reads its fields back into this, which nothing can send.
/// </summary>
/// <param name="Side">The side of the order to send, or null for a cancellation.</param>
/// <param name="Quantity">In the base asset.</param>
/// <param name="Notional">In the quote asset, as are every price and <paramref name="MoneyAtRisk"/>.</param>
public sealed record TracedOrder(
    SizedOrderKind Kind,
    IntentAction Action,
    IntentOrigin Origin,
    Market Market,
    OrderSide? Side,
    bool ReduceOnly,
    EntryType? EntryType,
    decimal? Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    decimal? TargetPrice,
    decimal? Notional,
    decimal? MoneyAtRisk,
    decimal? RewardToRisk,
    int? Leverage,
    decimal? EstimatedLiquidationPrice);

/// <summary>A Confirm agent's approval of the cycle's order, as the approvals page shows it, with its second look in full.</summary>
/// <param name="RevalidationRules">
/// Every rule of the Risk Engine's second look when it was approved (FR-APR-03), or empty when it wasn't checked again.
/// <see cref="ApprovalRevalidation"/> keeps only the failures.
/// </param>
public sealed record ApprovalTrace(ApprovalRecord Record, IReadOnlyList<RuleResult> RevalidationRules)
{
    /// <summary>Leaves out the proposal, which models wrote (FR-LOG-07).</summary>
    public override string ToString() => $"{nameof(ApprovalTrace)} {Record.Id} {Record.Status}";
}
