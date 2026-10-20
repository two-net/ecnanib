using Ecnanib.Agents.Abstractions;

namespace Ecnanib.Strategies.Catalog;

/// <summary>
/// Appendix A, "common parameters (all strategies)", and the strategy-specific parameters several strategies share,
/// with their defaults and bounds (FR-STR-04). Risk per trade, open positions and the daily loss limit are the
/// agent's risk limits, not parameters.
/// </summary>
internal static class CommonParameters
{
    public static IEnumerable<StrategyParameter> Common(decimal partialFraction, int entryTimeout) =>
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

    public static StrategyParameter SwingLookback(int @default = 3) => StrategyParameter.Integer(SpecificParameters.SwingLookback, "Swing lookback (candles each side)", @default, 1, 20);

    public static StrategyParameter BiasEmaPeriod(string label, int @default) => StrategyParameter.Integer(SpecificParameters.BiasEmaPeriod, label, @default, 2, 400);

    public static StrategyParameter PullbackToleranceAtr(string label = "Pullback tolerance (× ATR)") => StrategyParameter.Decimal(SpecificParameters.PullbackToleranceAtr, label, 0.25m, 0m, 3m);

    public static StrategyParameter PullbackLookback(int @default) => StrategyParameter.Integer(SpecificParameters.PullbackLookback, "Pullback lookback (candles)", @default, 1, 50);

    public static StrategyParameter VolumeAveragePeriod() => StrategyParameter.Integer(SpecificParameters.VolumeAveragePeriod, "Volume average period", 20, 2, 200);

    public static StrategyParameter VolumeMultiple(decimal @default = 1.2m) => StrategyParameter.Decimal(SpecificParameters.VolumeMultiple, "Trigger volume multiple", @default, 0m, 10m);

    public static StrategyParameter PartialTakeProfitR(decimal @default) => StrategyParameter.Decimal(SpecificParameters.PartialTakeProfitR, "Partial take-profit (R)", @default, 0.5m, 10m);

    public static StrategyParameter MinRewardR(decimal @default) => StrategyParameter.Decimal(SpecificParameters.MinRewardR, "Minimum reward (R)", @default, 0.5m, 10m);

    public static StrategyParameter TypicalRewardR(decimal @default = 3m) => StrategyParameter.Decimal(SpecificParameters.TypicalRewardR, "Typical reward when no swing is in reach (R)", @default, 0.5m, 20m);

    public static StrategyParameter MaxStopDistanceAtr(decimal @default) => StrategyParameter.Decimal(SpecificParameters.MaxStopDistanceAtr, "Maximum stop distance (× ATR)", @default, 0.5m, 20m);

    public static StrategyParameter WeekendSizeMultiplier() => StrategyParameter.Decimal(SpecificParameters.WeekendSizeMultiplier, "Weekend size multiplier", 0.5m, 0m, 1m);

    public static StrategyParameter RangeMinCandles(int @default) => StrategyParameter.Integer(SpecificParameters.RangeMinCandles, "Range length at least (candles)", @default, 10, 500);

    public static StrategyParameter RangeMaxHeightAtr(decimal @default) => StrategyParameter.Decimal(SpecificParameters.RangeMaxHeightAtr, "Range height at most (× ATR)", @default, 0.5m, 30m);

    public static StrategyParameter RetestMaxCandles(int @default) => StrategyParameter.Integer(SpecificParameters.RetestMaxCandles, "Retest within (candles)", @default, 1, 100);

    public static StrategyParameter BtcRegimeEmaPeriod() => StrategyParameter.Integer(SpecificParameters.BtcRegimeEmaPeriod, "BTC regime EMA period (weekly)", 20, 2, 100);

    public static StrategyParameter RegimeEmaPeriod() => StrategyParameter.Integer(SpecificParameters.RegimeEmaPeriod, "Regime EMA period (daily)", 200, 50, 400);

    public static StrategyParameter RegimeRisingLookback() => StrategyParameter.Integer(SpecificParameters.RegimeRisingLookback, "Regime EMA rising over (days)", 10, 1, 100);

    public static StrategyParameter MaxExtensionPercent() => StrategyParameter.Decimal(SpecificParameters.MaxExtensionPercent, "Maximum extension above EMA50 (%)", 15m, 1m, 100m);

    public static StrategyParameter MinDailyQuoteVolume() => StrategyParameter.Decimal(SpecificParameters.MinDailyQuoteVolume, "Minimum average daily quote volume (non-BTC/ETH)", 20_000_000m, 0m, 1_000_000_000_000m);
}
