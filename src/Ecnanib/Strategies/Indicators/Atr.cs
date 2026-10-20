using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Indicators;

/// <summary>Average true range with Wilder's smoothing: the first value at index <c>period</c> is the mean of the true ranges 1..period.</summary>
internal static class Atr
{
    public static decimal?[] Compute(IReadOnlyList<Candle> candles, int period)
    {
        var result = new decimal?[candles.Count];
        if (period < 1 || candles.Count < period + 1)
            return result;

        decimal sum = 0;
        for (var i = 1; i <= period; i++)
            sum += TrueRange(candles, i);
        var atr = sum / period;
        result[period] = atr;
        for (var i = period + 1; i < candles.Count; i++)
        {
            atr = (atr * (period - 1) + TrueRange(candles, i)) / period;
            result[i] = atr;
        }

        return result;
    }

    public static decimal? Last(IReadOnlyList<Candle> candles, int period) =>
        candles.Count == 0 ? null : Compute(candles, period)[^1];

    /// <summary>max(high − low, |high − previous close|, |low − previous close|) for <paramref name="index"/> ≥ 1.</summary>
    public static decimal TrueRange(IReadOnlyList<Candle> candles, int index)
    {
        var candle = candles[index];
        var previousClose = candles[index - 1].Close;
        return Math.Max(candle.High - candle.Low, Math.Max(Math.Abs(candle.High - previousClose), Math.Abs(candle.Low - previousClose)));
    }
}
