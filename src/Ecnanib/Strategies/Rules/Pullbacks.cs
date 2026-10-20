using Ecnanib.Strategies.Engine;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Rules;

/// <summary>A named level per candle (null where undefined); a static level repeats one price.</summary>
internal sealed record LevelSeries(string Name, decimal?[] Values)
{
    public static LevelSeries Constant(string name, decimal price, int count) =>
        new(name, Enumerable.Repeat<decimal?>(price, count).ToArray());
}

internal sealed record Pullback(string Level, decimal LevelPrice, decimal Extreme);

/// <summary>
/// A pullback to a level: within the last <c>lookback + 1</c> candles price came within the tolerance of (or through)
/// the level and no close went more than the tolerance beyond it. Levels are tried in the strategy's priority order.
/// </summary>
internal static class Pullbacks
{
    public static Pullback? Find(IReadOnlyList<Candle> candles, IReadOnlyList<LevelSeries> levels, int lookback, decimal tolerance, Direction d)
    {
        var n = candles.Count;
        if (n == 0)
            return null;
        var from = Math.Max(0, n - 1 - lookback);
        foreach (var level in levels)
        {
            var touched = false;
            var held = true;
            for (var j = from; j < n && held; j++)
            {
                if (level.Values[j] is not { } value)
                    continue;
                if (d.Sign * (d.Extreme(candles[j]) - value) <= tolerance)
                    touched = true;
                if (d.Sign * (candles[j].Close - value) < -tolerance)
                    held = false;
            }

            if (touched && held && level.Values[n - 1] is { } current)
                return new Pullback(level.Name, current, Extreme(candles, from, n, d));
        }

        return null;
    }

    /// <summary>The lowest low (long) or highest high (short) of <c>[from, to)</c>.</summary>
    public static decimal Extreme(IReadOnlyList<Candle> candles, int from, int to, Direction d)
    {
        var extreme = d.Extreme(candles[from]);
        for (var j = from + 1; j < to; j++)
            extreme = d.IsLong ? Math.Min(extreme, candles[j].Low) : Math.Max(extreme, candles[j].High);
        return extreme;
    }
}
