using Ecnanib.Kernel;

namespace Ecnanib.Modules.MarketData.Contracts;

/// <summary>
/// How long one candle of a timeframe lasts, as Binance aligns it, for whatever counts in candles, such as a limit
/// entry's time-to-live (FR-EXE-01). Kernel holds the vocabulary without behaviour (rule 5 in docs/architecture.md).
/// </summary>
public static class TimeframeLength
{
    public static TimeSpan Of(Timeframe timeframe) => Timeframes.Length(Timeframes.Interval(timeframe));
}
