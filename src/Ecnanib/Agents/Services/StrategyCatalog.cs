using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Agents.Services;

/// <summary>
/// Appendix A as descriptors: one strategy for 15m and two for each of 1H, 4H and 1D (FR-AGT-03), all at version 1
/// (FR-STR-06), each with the common parameters and their defaults (FR-STR-04). Strategy-specific parameters arrive
/// with the engines.
/// </summary>
internal sealed class StrategyCatalog : IStrategyCatalog
{
    public const int CurrentVersion = 1;

    private readonly IReadOnlyList<StrategyDescriptor> _all;

    public StrategyCatalog(IOptions<AgentsOptions> options)
    {
        var entryTimeout = options.Value.DefaultEntryTimeoutCandles;
        _all =
        [
            Describe("intraday-trend-pullback", "Intraday Trend Pullback", CandleInterval.FifteenMinutes, 0.5m, entryTimeout,
                "4H bias from EMA50 and structure; pullback to the 15m EMA20/EMA50, session VWAP or the last breakout level; trigger on a close beyond the prior candle with 1.2× volume; stop beyond the pullback extreme; 50% off at 1.5R, trail the rest."),
            Describe("trend-pullback", "Trend Pullback", CandleInterval.OneHour, 0.33m, entryTimeout,
                "Daily bias from EMA20 and structure; pullback to the 1H EMA20/EMA50 or breakout level; trigger on a 1H close in the trend direction; stop beyond the swing; one third off at 2R, breakeven, trail behind swings."),
            Describe("range-breakout-retest", "Range Breakout–Retest", CandleInterval.OneHour, 0.33m, entryTimeout,
                "Range of at least 24 candles; 1H close outside on volume; retest holds; stop beyond the retest extreme; one third off at 2R, breakeven, trail behind swings."),
            Describe("swing-pullback", "Swing Pullback", CandleInterval.FourHours, 0.33m, entryTimeout,
                "Weekly structure and weekly EMA20 with the daily agreeing; pullback to the 4H EMA50 or breakout level; trigger on a 4H close in the trend direction; stop beyond the swing; one third off at 3R, breakeven, trail behind 4H swings or EMA50."),
            Describe("consolidation-breakout", "Consolidation Breakout", CandleInterval.FourHours, 0.33m, entryTimeout,
                "Range of at least 42 candles; 4H close outside, optional retest; stop beyond the range boundary; one third off at 3R, breakeven, trail."),
            Describe("position-breakout", "Position Breakout", CandleInterval.OneDay, 0.33m, entryTimeout,
                "Rising daily EMA200 with price above and weekly higher highs and lows; daily close above a consolidation of at least 20 days with volume expansion; stop beyond the swing; one third off at 3R, breakeven, trail under daily swings or EMA50."),
            Describe("ema-pullback", "EMA Pullback", CandleInterval.OneDay, 0.33m, entryTimeout,
                "Same regime as the position breakout; daily close holding EMA20 or EMA50 in an uptrend; stop beyond the swing; one third off at 3R, breakeven, trail."),
        ];
    }

    public IReadOnlyList<StrategyDescriptor> All => _all;

    public IReadOnlyList<StrategyDescriptor> ForTimeframe(CandleInterval timeframe) => _all.Where(s => s.Timeframe == timeframe).ToList();

    public StrategyDescriptor? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _all.FirstOrDefault(s => string.Equals(s.Id, id.Trim(), StringComparison.Ordinal));

    private static StrategyDescriptor Describe(string id, string name, CandleInterval timeframe, decimal partialFraction, int entryTimeout, string summary) =>
        new(id, name, timeframe, CurrentVersion, summary, Common(partialFraction, entryTimeout));

    /// <summary>Appendix A, "common parameters (all strategies)"; risk per trade, open positions and the daily loss limit are the agent's risk limits, not parameters.</summary>
    private static IReadOnlyList<StrategyParameter> Common(decimal partialFraction, int entryTimeout) =>
    [
        StrategyParameter.Integer(StrategyParameters.AtrPeriod, "ATR period", 14, 2, 200),
        StrategyParameter.Integer(StrategyParameters.EmaFastPeriod, "Fast EMA period", 20, 2, 400),
        StrategyParameter.Integer(StrategyParameters.EmaSlowPeriod, "Slow EMA period", 50, 2, 400),
        StrategyParameter.Decimal(StrategyParameters.StopBufferAtr, "Stop buffer (× ATR)", 1.0m, 0m, 5m),
        StrategyParameter.Decimal(StrategyParameters.PartialTakeProfitFraction, "Partial take-profit fraction", partialFraction, 0m, 1m),
        StrategyParameter.Choice(StrategyParameters.TrailingMethod, "Trailing method", StrategyParameters.TrailingBySwing, StrategyParameters.TrailingBySwing, StrategyParameters.TrailingByEma),
        StrategyParameter.Integer(StrategyParameters.EntryTimeoutCandles, "Entry timeout (candles)", entryTimeout, 1, 100),
        StrategyParameter.Flag(StrategyParameters.MarketFallback, "Market fallback after the entry timeout", false),
    ];
}
