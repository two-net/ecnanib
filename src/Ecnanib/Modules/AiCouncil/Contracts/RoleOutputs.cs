using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.AiCouncil.Contracts;

/// <summary>
/// A role's answer, read and checked against its output contract (Appendix B, FR-PRM-08). Only
/// <see cref="OutputContracts"/> makes one from a model's answer, and a field the answer's action doesn't use is
/// cleared, so nothing reads it by mistake. A model wrote every value, so it's never trusted further than its
/// contract: a trade proposal still goes through the Risk Engine (NFR-SEC-08). ToString shows no text a model
/// wrote (FR-LOG-07). See Output contracts in docs/architecture.md.
/// </summary>
public abstract record RoleOutput
{
    /// <summary>The role whose contract this is.</summary>
    public abstract AiRole Role { get; }
}

/// <summary>The Technical Analyst's answer.</summary>
/// <param name="HtfTrend">The trend on the higher timeframe.</param>
/// <param name="SupportLevels">Nearest to the current price first.</param>
/// <param name="ResistanceLevels">Nearest to the current price first.</param>
/// <param name="Confidence">From 0 to 1.</param>
/// <param name="Summary">At most <see cref="OutputContracts.SummaryMaxLength"/> characters.</param>
public sealed record TechnicalAssessment(
    Trend Trend,
    Trend HtfTrend,
    IReadOnlyList<decimal> SupportLevels,
    IReadOnlyList<decimal> ResistanceLevels,
    IReadOnlyList<TechnicalSignal> Signals,
    VolatilityRegime VolatilityRegime,
    bool SetupPresent,
    decimal Confidence,
    string Summary) : RoleOutput
{
    public override AiRole Role => AiRole.TechnicalAnalyst;

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{nameof(TechnicalAssessment)} {Trend}, higher timeframe {HtfTrend}, {Signals.Count} signals, setup {SetupPresent}");
}

/// <param name="Strength">From 0 to 1.</param>
public sealed record TechnicalSignal(string Name, SignalDirection Direction, decimal Strength)
{
    public override string ToString() => $"{nameof(TechnicalSignal)} {Direction}";
}

/// <summary>The Sentiment Analyst's answer.</summary>
/// <param name="Score">From −1, very bearish, to +1, very bullish.</param>
/// <param name="DataFreshness">When the newest source was published, or null without sources.</param>
/// <param name="Confidence">From 0 to 1.</param>
/// <param name="Summary">At most <see cref="OutputContracts.SummaryMaxLength"/> characters.</param>
public sealed record SentimentAssessment(
    decimal Score,
    IReadOnlyList<string> Drivers,
    IReadOnlyList<SentimentSource> Sources,
    DateTimeOffset? DataFreshness,
    decimal Confidence,
    string Summary) : RoleOutput
{
    public override AiRole Role => AiRole.SentimentAnalyst;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(SentimentAssessment)}, {Drivers.Count} drivers, {Sources.Count} sources");
}

/// <param name="Url">An absolute http or https URL. A model wrote it, so it's never fetched or shown as a link without care.</param>
public sealed record SentimentSource(string Title, string Url, DateTimeOffset? PublishedAt)
{
    public override string ToString() => nameof(SentimentSource);
}

/// <summary>
/// The Strategist's answer. Fields the action doesn't use are null, or empty for <see cref="TargetPrices"/>. Every
/// price is in the quote asset. The decision cycle copies what the Risk Engine needs into an
/// <see cref="OrderIntent"/>, which the engine checks again (rule 4 in docs/architecture.md).
/// </summary>
/// <param name="Side">Entries only: long for BUY and OPEN_LONG, short for OPEN_SHORT.</param>
/// <param name="EntryType">Entries only.</param>
/// <param name="EntryPrice">A limit entry's price.</param>
/// <param name="StopPrice">An entry's protective stop, or ADJUST_STOP's new stop.</param>
/// <param name="TargetPrices">Nearest first, for an entry or ADJUST_TARGET, where empty removes the target.</param>
/// <param name="RiskPercent">Entries only: above 0 and at most the agent's maximum risk per trade.</param>
/// <param name="LimitTtlCandles">A limit entry's life in candles of the agent's timeframe, at least 1.</param>
/// <param name="CloseFraction">SELL and CLOSE only: above 0 and at most 1, where null closes all of it.</param>
/// <param name="Invalidation">The condition that would prove the idea wrong. Required for an entry.</param>
/// <param name="Confidence">From 0 to 1.</param>
public sealed record TradeProposal(
    TradeAction Action,
    PositionSide? Side,
    EntryType? EntryType,
    decimal? EntryPrice,
    decimal? StopPrice,
    IReadOnlyList<decimal> TargetPrices,
    decimal? RiskPercent,
    int? LimitTtlCandles,
    decimal? CloseFraction,
    string? Invalidation,
    string Rationale,
    decimal Confidence) : RoleOutput
{
    public override AiRole Role => AiRole.Strategist;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(TradeProposal)} {Action} {Side} {EntryType}, {TargetPrices.Count} targets");
}

/// <summary>The Risk Critic's answer.</summary>
/// <param name="Modifications">Only for <see cref="VerdictKind.Modify"/>, and every change reduces risk.</param>
public sealed record CriticVerdict(
    VerdictKind Verdict,
    VerdictModifications? Modifications,
    IReadOnlyList<string> Concerns,
    RiskChecklist Checklist,
    string Rationale) : RoleOutput
{
    public override AiRole Role => AiRole.RiskCritic;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(CriticVerdict)} {Verdict}, {Concerns.Count} concerns");
}

/// <summary>
/// The Risk Critic's changes to the proposal, each null, or empty for the targets, when it isn't changed. Checked
/// against the proposal, each one reduces risk: a tighter stop, a lower risk percent, or no farther targets.
/// </summary>
public sealed record VerdictModifications(decimal? StopPrice, IReadOnlyList<decimal> TargetPrices, decimal? RiskPercent);

/// <summary>The Risk Critic's checklist (Appendix B).</summary>
public sealed record RiskChecklist(
    ChecklistItem TrendAlignment,
    ChecklistItem RewardToRisk,
    ChecklistItem EventRisk,
    ChecklistItem Funding,
    ChecklistItem Reflections);

public sealed record ChecklistItem(ChecklistResult Result, string Note)
{
    public override string ToString() => $"{nameof(ChecklistItem)} {Result}";
}

/// <summary>The Reviewer's lesson from a closed trade (FR-EXP-04).</summary>
/// <param name="Outcome">What the model copied from the trade's record. The trade journal's own figures are the ones to keep (#45).</param>
/// <param name="Lesson">One sentence that a future decision can act on.</param>
public sealed record TradeReflection(
    string SetupSummary,
    TradeOutcome Outcome,
    IReadOnlyList<string> WhatWorked,
    IReadOnlyList<string> WhatFailed,
    string Lesson,
    IReadOnlyList<string> Tags) : RoleOutput
{
    public override AiRole Role => AiRole.Reviewer;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(TradeReflection)}, {Lesson.Length} characters of lesson, {Tags.Count} tags");
}

/// <param name="RMultiple">The result in multiples of what the trade risked, or null when it isn't known.</param>
/// <param name="Pnl">Realized P&amp;L in the quote asset.</param>
public sealed record TradeOutcome(decimal? RMultiple, decimal Pnl);

/// <summary>Stored by name, so never renamed.</summary>
public enum Trend
{
    Up,
    Down,
    Range,
}

/// <summary>Stored by name, so never renamed.</summary>
public enum SignalDirection
{
    Bullish,
    Bearish,
    Neutral,
}

/// <summary>Stored by name, so never renamed.</summary>
public enum VolatilityRegime
{
    Low,
    Normal,
    High,
}

/// <summary>
/// FR-CYC-03's actions, as the Strategist proposes them. Models write them as the SRS does, such as OPEN_LONG.
/// <see cref="TradeActions.For"/> says which a market allows. Stored by name, so never renamed.
/// </summary>
public enum TradeAction
{
    /// <summary>Do nothing this cycle.</summary>
    Hold,

    /// <summary>Spot: open or add to a long.</summary>
    Buy,

    /// <summary>Spot: close or reduce the long.</summary>
    Sell,

    /// <summary>Futures: open or add to a long.</summary>
    OpenLong,

    /// <summary>Futures: open or add to a short.</summary>
    OpenShort,

    /// <summary>Futures: close or reduce the position, reduce-only.</summary>
    Close,

    /// <summary>Move the protective stop, only toward less risk.</summary>
    AdjustStop,

    /// <summary>Move or remove the take-profit.</summary>
    AdjustTarget,

    /// <summary>Cancel the unfilled entry order.</summary>
    CancelPending,
}

/// <summary>Stored by name, so never renamed.</summary>
public enum VerdictKind
{
    Approve,
    Modify,
    Reject,
}

/// <summary>Stored by name, so never renamed.</summary>
public enum ChecklistResult
{
    Pass,
    Fail,
    NotApplicable,
}

/// <summary>Which of FR-CYC-03's actions each market allows, in the order the market overlays list them.</summary>
public static class TradeActions
{
    private static readonly TradeAction[] Spot =
        [TradeAction.Buy, TradeAction.Sell, TradeAction.AdjustStop, TradeAction.AdjustTarget, TradeAction.CancelPending, TradeAction.Hold];

    private static readonly TradeAction[] Futures =
    [
        TradeAction.OpenLong, TradeAction.OpenShort, TradeAction.Close, TradeAction.AdjustStop, TradeAction.AdjustTarget,
        TradeAction.CancelPending, TradeAction.Hold,
    ];

    public static IReadOnlyList<TradeAction> For(Market market) => market switch
    {
        Market.Spot => Spot,
        Market.Futures => Futures,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, "Not a market."),
    };

    /// <summary>BUY, OPEN_LONG and OPEN_SHORT, which open or add to a position.</summary>
    public static bool IsEntry(TradeAction action) => action is TradeAction.Buy or TradeAction.OpenLong or TradeAction.OpenShort;

    /// <summary>SELL and CLOSE, which close all or part of the position.</summary>
    public static bool IsExit(TradeAction action) => action is TradeAction.Sell or TradeAction.Close;

    /// <summary>The action as the SRS and the models write it, such as OPEN_LONG.</summary>
    public static string Name(TradeAction action) => ContractNames.Action(action);
}
