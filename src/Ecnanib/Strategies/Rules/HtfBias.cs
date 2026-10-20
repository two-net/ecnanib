using Ecnanib.Agents.Abstractions;
using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Rules;

/// <summary>Higher-timeframe bias from an EMA relation and swing structure (Appendix A "bias" and "regime" rows), judged on closed HTF candles against the latest own close.</summary>
internal static class HtfBias
{
    /// <summary>Long iff price above the EMA with higher highs and lows; Short iff below with lower highs and lows; else None.</summary>
    public static BiasDirection Bias(IReadOnlyList<Candle> htf, decimal price, int emaPeriod, int swingLookback, out decimal? ema, out SwingStructure structure)
    {
        ema = Ema.Last(htf, emaPeriod);
        structure = Swings.Classify(Swings.Find(htf, swingLookback));
        if (ema is null)
            return BiasDirection.None;
        if (price > ema && structure == SwingStructure.HigherHighsHigherLows)
            return BiasDirection.Long;
        if (price < ema && structure == SwingStructure.LowerHighsLowerLows)
            return BiasDirection.Short;
        return BiasDirection.None;
    }

    /// <summary>"The daily agrees": price beyond the daily EMA and the daily structure not the opposite one.</summary>
    public static bool Agrees(IReadOnlyList<Candle> daily, decimal price, int emaPeriod, int swingLookback, Direction d, out decimal? ema, out SwingStructure structure)
    {
        ema = Ema.Last(daily, emaPeriod);
        structure = Swings.Classify(Swings.Find(daily, swingLookback));
        if (ema is null)
            return false;
        var opposite = d.IsLong ? SwingStructure.LowerHighsLowerLows : SwingStructure.HigherHighsHigherLows;
        return d.Beyond(price, ema.Value) && structure != opposite;
    }

    /// <summary>The 1D regime: a rising daily EMA with the close above it and weekly higher highs and lows (mirrored for Short).</summary>
    public static BiasDirection Regime(IReadOnlyList<Candle> daily, IReadOnlyList<Candle> weekly, int emaPeriod, int risingLookback, int swingLookback, out decimal?[] ema, out SwingStructure weeklyStructure)
    {
        ema = Ema.Compute(daily, emaPeriod);
        weeklyStructure = Swings.Classify(Swings.Find(weekly, swingLookback));
        if (daily.Count == 0 || ema[^1] is not { } last)
            return BiasDirection.None;
        var close = daily[^1].Close;
        if (Ema.IsRising(ema, risingLookback) == true && close > last && weeklyStructure == SwingStructure.HigherHighsHigherLows)
            return BiasDirection.Long;
        if (Ema.IsFalling(ema, risingLookback) == true && close < last && weeklyStructure == SwingStructure.LowerHighsLowerLows)
            return BiasDirection.Short;
        return BiasDirection.None;
    }
}
