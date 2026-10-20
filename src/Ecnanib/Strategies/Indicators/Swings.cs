using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Indicators;

internal enum SwingKind
{
    High,
    Low,
}

/// <summary>Market structure from the last two confirmed swing highs and lows.</summary>
internal enum SwingStructure
{
    None,
    HigherHighsHigherLows,
    LowerHighsLowerLows,
}

internal sealed record SwingPoint(int Index, decimal Price, SwingKind Kind);

/// <summary>
/// Fractal swing points: a swing high is strictly above the <c>lookback</c> highs before it and at or above the
/// <c>lookback</c> highs after it (of equal highs the earlier wins); lows mirrored. A point is confirmed only once
/// <c>lookback</c> candles have closed after it.
/// </summary>
internal static class Swings
{
    public static IReadOnlyList<SwingPoint> Find(IReadOnlyList<Candle> candles, int lookback)
    {
        var points = new List<SwingPoint>();
        if (lookback < 1)
            return points;

        for (var i = lookback; i < candles.Count - lookback; i++)
        {
            var high = candles[i].High;
            var low = candles[i].Low;
            var isHigh = true;
            var isLow = true;
            for (var j = i - lookback; j < i && (isHigh || isLow); j++)
            {
                if (candles[j].High >= high)
                    isHigh = false;
                if (candles[j].Low <= low)
                    isLow = false;
            }

            for (var j = i + 1; j <= i + lookback && (isHigh || isLow); j++)
            {
                if (candles[j].High > high)
                    isHigh = false;
                if (candles[j].Low < low)
                    isLow = false;
            }

            if (isHigh)
                points.Add(new SwingPoint(i, high, SwingKind.High));
            if (isLow)
                points.Add(new SwingPoint(i, low, SwingKind.Low));
        }

        return points;
    }

    public static SwingStructure Classify(IReadOnlyList<SwingPoint> points)
    {
        var highs = points.Where(p => p.Kind == SwingKind.High).TakeLast(2).ToList();
        var lows = points.Where(p => p.Kind == SwingKind.Low).TakeLast(2).ToList();
        if (highs.Count < 2 || lows.Count < 2)
            return SwingStructure.None;
        if (highs[1].Price > highs[0].Price && lows[1].Price > lows[0].Price)
            return SwingStructure.HigherHighsHigherLows;
        if (highs[1].Price < highs[0].Price && lows[1].Price < lows[0].Price)
            return SwingStructure.LowerHighsLowerLows;
        return SwingStructure.None;
    }

    public static SwingPoint? Last(IReadOnlyList<SwingPoint> points, SwingKind kind) =>
        points.LastOrDefault(p => p.Kind == kind);

    /// <summary>1 for higher highs and lows, −1 for lower, 0 otherwise; the indicator form.</summary>
    public static int Sign(SwingStructure structure) => structure switch
    {
        SwingStructure.HigherHighsHigherLows => 1,
        SwingStructure.LowerHighsLowerLows => -1,
        _ => 0,
    };
}
