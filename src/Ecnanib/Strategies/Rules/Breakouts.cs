using Ecnanib.Strategies.Engine;
using Ecnanib.Strategies.Indicators;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Rules;

internal sealed record Breakout(int Index, RangeBox Box, decimal Boundary);

internal sealed record Retest(decimal Extreme);

/// <summary>Range breakouts, their retests and the "breakout level" a pullback may return to.</summary>
internal static class Breakouts
{
    /// <summary>The most recent confirmed swing high below price (the last broken resistance) for a long; swing low above price for a short.</summary>
    public static decimal? BreakoutLevel(IReadOnlyList<SwingPoint> swings, decimal price, Direction d)
    {
        var kind = d.IsLong ? SwingKind.High : SwingKind.Low;
        decimal? level = null;
        foreach (var point in swings)
        {
            if (point.Kind == kind && d.Beyond(price, point.Price))
                level = point.Price;
        }

        return level;
    }

    /// <summary>
    /// The latest candle at or before <paramref name="lastIndex"/> and within <paramref name="maxAge"/> candles of it
    /// that closed outside a valid box of <paramref name="minCandles"/> candles, with volume expansion when
    /// <paramref name="volumePeriod"/> is given. The current ATR applies to every box.
    /// </summary>
    public static Breakout? Find(IReadOnlyList<Candle> candles, decimal atr, Direction d, int minCandles, decimal maxHeightAtr, int lastIndex, int maxAge, int? volumePeriod, decimal volumeMultiple)
    {
        var first = Math.Max(minCandles, lastIndex - maxAge);
        for (var b = Math.Min(lastIndex, candles.Count - 1); b >= first; b--)
        {
            var box = Ranges.Box(candles, b, minCandles, maxHeightAtr, atr);
            if (box is null)
                continue;
            var boundary = d.IsLong ? box.Top : box.Bottom;
            if (!d.Beyond(candles[b].Close, boundary))
                continue;
            if (volumePeriod is { } period)
            {
                var ratio = Triggers.VolumeRatio(candles, b, period);
                if (ratio is null || ratio < volumeMultiple)
                    continue;
            }

            return new Breakout(b, box, boundary);
        }

        return null;
    }

    /// <summary>
    /// After the breakout, price came back within the tolerance of the boundary, no close went more than the tolerance
    /// back inside, and the last candle holds the level. Needs at least one candle after the breakout.
    /// </summary>
    public static Retest? Retest(IReadOnlyList<Candle> candles, Breakout breakout, decimal tolerance, Direction d)
    {
        var n = candles.Count;
        if (breakout.Index > n - 2)
            return null;
        var touched = false;
        for (var j = breakout.Index + 1; j < n; j++)
        {
            if (d.Sign * (d.Extreme(candles[j]) - breakout.Boundary) <= tolerance)
                touched = true;
            if (d.Sign * (candles[j].Close - breakout.Boundary) < -tolerance)
                return null;
        }

        if (!touched || !Triggers.HoldsLevel(candles[n - 1], breakout.Boundary, d))
            return null;
        return new Retest(Pullbacks.Extreme(candles, breakout.Index + 1, n, d));
    }
}
