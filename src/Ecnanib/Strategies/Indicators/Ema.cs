using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Strategies.Indicators;

/// <summary>Exponential moving average of closes, seeded with the simple average of the first <c>period</c> closes (the TA-Lib convention).</summary>
internal static class Ema
{
    /// <summary>The series aligned to the candles; null before index <c>period − 1</c>.</summary>
    public static decimal?[] Compute(IReadOnlyList<Candle> candles, int period)
    {
        var result = new decimal?[candles.Count];
        if (period < 1 || candles.Count < period)
            return result;

        decimal sum = 0;
        for (var i = 0; i < period; i++)
            sum += candles[i].Close;
        var ema = sum / period;
        result[period - 1] = ema;
        var k = 2m / (period + 1);
        for (var i = period; i < candles.Count; i++)
        {
            ema += k * (candles[i].Close - ema);
            result[i] = ema;
        }

        return result;
    }

    public static decimal? Last(IReadOnlyList<Candle> candles, int period) =>
        candles.Count == 0 ? null : Compute(candles, period)[^1];

    /// <summary>True when the last value is strictly above the one <paramref name="lookback"/> candles earlier; null when either is undefined.</summary>
    public static bool? IsRising(decimal?[] series, int lookback) => Compare(series, lookback) is { } c ? c > 0 : null;

    public static bool? IsFalling(decimal?[] series, int lookback) => Compare(series, lookback) is { } c ? c < 0 : null;

    /// <summary>The change over <paramref name="lookback"/> candles, or null when undefined.</summary>
    public static decimal? Change(decimal?[] series, int lookback) => Compare(series, lookback);

    private static decimal? Compare(decimal?[] series, int lookback)
    {
        if (lookback < 1 || series.Length <= lookback)
            return null;
        var now = series[^1];
        var then = series[^(1 + lookback)];
        return now is null || then is null ? null : now.Value - then.Value;
    }
}
