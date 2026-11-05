using Ecnanib.Kernel;

namespace Ecnanib.Modules.RiskEngine.Contracts;

/// <summary>
/// One evaluation of an intent, for the decision trace (FR-RISK-10, SRS 6.1 RiskEvaluation). Every rule is
/// listed, in <see cref="RiskRule"/> order, whether it applied or not, so the trace shows what was checked.
/// </summary>
/// <param name="Caps">The caps it applied.</param>
/// <param name="Effective">The agent's settings with the caps applied, which is what the rules used.</param>
/// <param name="Order">What to send, when it passed. Never set when it failed.</param>
/// <param name="Breach">A loss limit the agent reached, which the caller pauses it for (FR-RISK-08).</param>
public sealed record RiskEvaluation(
    RiskVerdict Verdict,
    OrderIntent Intent,
    RiskCaps Caps,
    RiskSettings Effective,
    IReadOnlyList<RuleResult> Rules,
    SizedOrder? Order,
    LossLimitBreach? Breach)
{
    public bool Passed => Verdict == RiskVerdict.Pass;

    /// <summary>The rules that failed, in order.</summary>
    public IEnumerable<RuleResult> Failures => Rules.Where(rule => rule.Outcome == RuleOutcome.Fail);
}

/// <summary>Stored by name, so never renamed.</summary>
public enum RiskVerdict
{
    Pass,
    Fail,
}

/// <param name="Reason">Why it failed. Null unless <paramref name="Outcome"/> is <see cref="RuleOutcome.Fail"/>.</param>
/// <param name="Inputs">What it looked at, in order, written in the invariant culture.</param>
public sealed record RuleResult(RiskRule Rule, RuleOutcome Outcome, RiskReason? Reason, IReadOnlyList<RuleInput> Inputs);

/// <summary>One value a rule looked at, such as ("Stop", "64250.0").</summary>
public sealed record RuleInput(string Name, string Value);

/// <summary>Stored by name, so never renamed.</summary>
public enum RuleOutcome
{
    Pass,
    Fail,

    /// <summary>The rule doesn't apply to this action or market.</summary>
    NotApplicable,

    /// <summary>A rule it depends on failed, so it had nothing to check.</summary>
    NotEvaluated,
}

/// <summary>The rules, in the order they run. Stored by name, so never renamed. See docs/architecture.md.</summary>
public enum RiskRule
{
    /// <summary>The settings are in range and the capital is above zero. The caps are applied (FR-RISK-11).</summary>
    Settings,

    /// <summary>The action suits the market and the position.</summary>
    Action,

    SymbolTrading,

    /// <summary>FR-RISK-08.</summary>
    DailyLoss,

    /// <summary>FR-RISK-08.</summary>
    WeeklyLoss,

    /// <summary>FR-RISK-08.</summary>
    ConsecutiveLosses,

    EntriesPerDay,

    /// <summary>Cycles to wait after a loss.</summary>
    Cooldown,

    /// <summary>No entry while another is unfilled.</summary>
    PendingEntry,

    /// <summary>Futures only.</summary>
    Funding,

    /// <summary>Rounding to the tick, and the price limits (FR-RISK-04).</summary>
    Prices,

    /// <summary>A protective stop is always required.</summary>
    StopRequired,

    /// <summary>FR-RISK-05.</summary>
    StopSide,

    /// <summary>FR-RISK-05.</summary>
    StopDistance,

    /// <summary>FR-RISK-06.</summary>
    StopNotWidened,

    TargetSide,

    RewardToRisk,

    /// <summary>FR-RISK-07.</summary>
    NoAveragingDown,

    /// <summary>FR-RISK-03.</summary>
    Size,

    /// <summary>The minimum quantity (FR-RISK-04).</summary>
    Quantity,

    /// <summary>The minimum and maximum notional (FR-RISK-04).</summary>
    Notional,

    /// <summary>Futures only.</summary>
    LiquidationBuffer,
}

/// <summary>Why a rule failed. Stored by name, so never renamed.</summary>
public enum RiskReason
{
    SettingsOutOfRange,
    NoCapital,

    /// <summary>A number in the inputs can't be right, such as a negative balance.</summary>
    InputInvalid,

    ActionNotForMarket,
    EntryTypeMissing,
    OppositePosition,
    NoPosition,
    NoPendingEntry,
    SymbolNotTrading,
    DailyLossLimitReached,
    WeeklyLossLimitReached,
    ConsecutiveLossesReached,
    EntriesPerDayReached,
    CoolingDown,
    EntryPending,
    FundingAgainstPosition,

    /// <summary>Binance sent no filter the rule needs.</summary>
    FilterMissing,

    PriceMissing,
    MarkPriceMissing,

    /// <summary>Outside PRICE_FILTER's minimum and maximum.</summary>
    PriceOutOfRange,

    /// <summary>Outside PERCENT_PRICE's band around the reference price.</summary>
    PriceOutsideBand,

    StopMissing,
    StopWrongSide,
    AtrUnavailable,
    StopTooClose,
    StopTooFar,
    StopWidened,
    StopUnchanged,
    TargetWrongSide,
    RewardToRiskTooLow,
    AveragingDown,
    RiskPercentInvalid,
    CloseFractionInvalid,

    /// <summary>The position already risks the whole budget at the new stop.</summary>
    BudgetUsed,

    /// <summary>A cap on notional, capital or balance leaves no room.</summary>
    NoCapacity,

    QuantityTooSmall,
    NotionalTooSmall,
    NotionalTooLarge,

    /// <summary>A Spot position too small for Binance to sell.</summary>
    Dust,

    /// <summary>No leverage bracket covers the position, so its liquidation price can't be estimated.</summary>
    LiquidationUnknown,

    LeverageAboveBracket,
    LiquidationTooClose,
}

/// <summary>What the loss limits found (FR-RISK-08).</summary>
public sealed record LossLimitCheck(IReadOnlyList<RuleResult> Rules, LossLimitBreach? Breach);

/// <summary>A loss limit the agent reached, which pauses it.</summary>
/// <param name="Until">When the pause ends. Null for the consecutive-loss count, which lasts until the user resumes.</param>
public sealed record LossLimitBreach(LossLimitKind Kind, DateTimeOffset? Until);

/// <summary>Stored by name, so never renamed.</summary>
public enum LossLimitKind
{
    Daily,
    Weekly,
    ConsecutiveLosses,
}

/// <summary>Stored by name, so never renamed.</summary>
public enum SizedOrderKind
{
    Entry,
    Exit,
    StopAdjustment,
    TargetAdjustment,
    CancelEntry,
}

/// <summary>Stored by name, so never renamed.</summary>
public enum OrderSide
{
    Buy,
    Sell,
}

/// <summary>
/// What a passing evaluation allows to be sent, rounded to the exchange's filters. Only the engine makes one: its
/// constructor is internal, a test bans calling it anywhere else, and it's a class rather than a record so
/// nothing can copy one with a change. Execution sends only these (AC-01). An entry's protective stop and target
/// are part of it, so they need no evaluation of their own.
/// </summary>
public sealed class SizedOrder
{
    internal SizedOrder(
        SizedOrderKind kind,
        IntentAction action,
        IntentOrigin origin,
        Market market,
        OrderSide? side,
        bool reduceOnly,
        EntryType? entryType = null,
        decimal? quantity = null,
        decimal? limitPrice = null,
        decimal? stopPrice = null,
        decimal? targetPrice = null,
        decimal? notional = null,
        decimal? moneyAtRisk = null,
        decimal? rewardToRisk = null,
        int? leverage = null,
        decimal? estimatedLiquidationPrice = null)
    {
        Kind = kind;
        Action = action;
        Origin = origin;
        Market = market;
        Side = side;
        ReduceOnly = reduceOnly;
        EntryType = entryType;
        Quantity = quantity;
        LimitPrice = limitPrice;
        StopPrice = stopPrice;
        TargetPrice = targetPrice;
        Notional = notional;
        MoneyAtRisk = moneyAtRisk;
        RewardToRisk = rewardToRisk;
        Leverage = leverage;
        EstimatedLiquidationPrice = estimatedLiquidationPrice;
    }

    public SizedOrderKind Kind { get; }

    public IntentAction Action { get; }

    public IntentOrigin Origin { get; }

    public Market Market { get; }

    /// <summary>
    /// The side of the order to send: an entry's own side, or the side that closes the position, which is also
    /// the side of a protective stop or target. Null for a cancellation, which names no side.
    /// </summary>
    public OrderSide? Side { get; }

    /// <summary>True for exits and for every protective order on Futures.</summary>
    public bool ReduceOnly { get; }

    /// <summary>Entries only.</summary>
    public EntryType? EntryType { get; }

    /// <summary>
    /// Entries and exits: the quantity to send, on the step grid. Stop and target adjustments: the position's
    /// quantity, which the new order covers. Null for a cancellation.
    /// </summary>
    public decimal? Quantity { get; }

    /// <summary>A limit entry's price, on the tick grid.</summary>
    public decimal? LimitPrice { get; }

    /// <summary>An entry's protective stop, or an adjustment's new one, on the tick grid.</summary>
    public decimal? StopPrice { get; }

    /// <summary>An entry's take-profit, or an adjustment's new one, on the tick grid. Null removes an adjusted target.</summary>
    public decimal? TargetPrice { get; }

    /// <summary>Entries: quantity × entry price.</summary>
    public decimal? Notional { get; }

    /// <summary>
    /// Entries: what the whole position would lose if the stop filled at its price, fees and slippage aside. That
    /// is at most the agent's risk per trade (FR-RISK-03).
    /// </summary>
    public decimal? MoneyAtRisk { get; }

    /// <summary>Entries with a target: the target's distance over the stop's.</summary>
    public decimal? RewardToRisk { get; }

    /// <summary>Futures entries: the leverage to set before sending (FR-RISK-09).</summary>
    public int? Leverage { get; }

    /// <summary>Futures entries: the engine's estimate of the position's liquidation price. Null when it can't be liquidated.</summary>
    public decimal? EstimatedLiquidationPrice { get; }
}
