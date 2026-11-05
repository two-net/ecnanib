namespace Ecnanib.Modules.MarketData.Contracts;

/// <summary>
/// The indicators computed for a series (FR-CYC-02 step 4). Each is null when the series is too short for it.
/// Prices are in the symbol's quote asset, and volumes in its base asset.
/// </summary>
/// <param name="Ema20">The exponential moving average of the close over 20 candles.</param>
/// <param name="Rsi14">Wilder's relative strength index over 14 candles, from 0 to 100.</param>
/// <param name="Atr14">Wilder's average true range over 14 candles.</param>
/// <param name="SwingHighs">The latest confirmed swing highs, newest first.</param>
/// <param name="SwingLows">The latest confirmed swing lows, newest first.</param>
public sealed record IndicatorSet(
    decimal? Ema20,
    decimal? Ema50,
    decimal? Ema200,
    decimal? Rsi14,
    decimal? Atr14,
    IReadOnlyList<SwingPoint> SwingHighs,
    IReadOnlyList<SwingPoint> SwingLows,
    VolumeStatistics? Volume);

/// <param name="OpenTime">The open time of the candle with the swing.</param>
/// <param name="Price">Its high for a swing high, or its low for a swing low.</param>
public sealed record SwingPoint(DateTimeOffset OpenTime, decimal Price);

/// <param name="Latest">The last candle's volume.</param>
/// <param name="Average">The mean volume of the last <see cref="Indicators.VolumePeriod"/> candles, the last included.</param>
/// <param name="Relative">Latest divided by Average. Null when the average is 0.</param>
public sealed record VolumeStatistics(decimal Latest, decimal Average, decimal? Relative);

/// <summary>
/// Deterministic indicators (FR-CYC-02 step 4): the same candles always give the same values, on any
/// machine, because every step is decimal arithmetic. Nothing is rounded. The moving averages, RSI and ATR
/// follow TA-Lib's definitions, which the tests check against. See Market data in docs/architecture.md.
/// </summary>
public static class Indicators
{
    public const int RsiPeriod = 14;
    public const int AtrPeriod = 14;

    /// <summary>How many candles on each side a swing must stand out from.</summary>
    public const int SwingStrength = 3;

    /// <summary>How many swing highs, and how many swing lows, are kept.</summary>
    public const int SwingCount = 5;

    public const int VolumePeriod = 20;

    /// <summary>Every indicator, for candles that are closed and oldest first.</summary>
    /// <exception cref="ArgumentException">The candles aren't in order of open time.</exception>
    public static IndicatorSet Compute(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        for (var i = 1; i < candles.Count; i++)
        {
            if (candles[i].OpenTime <= candles[i - 1].OpenTime)
            {
                throw new ArgumentException("Candles must be in order of open time, oldest first.", nameof(candles));
            }
        }

        var closes = candles.Select(candle => candle.Close).ToList();

        return new IndicatorSet(
            Ema(closes, 20),
            Ema(closes, 50),
            Ema(closes, 200),
            Rsi(closes, RsiPeriod),
            Atr(candles, AtrPeriod),
            SwingHighs(candles, SwingStrength, SwingCount),
            SwingLows(candles, SwingStrength, SwingCount),
            Volume(candles, VolumePeriod));
    }

    /// <summary>
    /// The exponential moving average at the last value. It starts from the simple average of the first
    /// <paramref name="period"/> values, then each value moves it by 2 / (period + 1) of the difference.
    /// Null with fewer than <paramref name="period"/> values.
    /// </summary>
    public static decimal? Ema(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        if (values.Count < period)
        {
            return null;
        }

        var ema = Sum(values, 0, period) / period;
        var weight = 2m / (period + 1);

        for (var i = period; i < values.Count; i++)
        {
            ema += weight * (values[i] - ema);
        }

        return ema;
    }

    /// <summary>
    /// Wilder's relative strength index at the last close: 100 × average gain / (average gain + average
    /// loss). The first averages are the simple means of the first <paramref name="period"/> changes, and
    /// each later change is smoothed in with weight 1 / period. A flat series is 50, where TA-Lib gives 0,
    /// because neither side has the strength. Null with fewer than period + 1 closes.
    /// </summary>
    public static decimal? Rsi(IReadOnlyList<decimal> closes, int period)
    {
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        if (closes.Count < period + 1)
        {
            return null;
        }

        decimal gain = 0, loss = 0;

        for (var i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            gain += Math.Max(change, 0);
            loss += Math.Max(-change, 0);
        }

        gain /= period;
        loss /= period;

        for (var i = period + 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            gain = ((gain * (period - 1)) + Math.Max(change, 0)) / period;
            loss = ((loss * (period - 1)) + Math.Max(-change, 0)) / period;
        }

        return gain + loss == 0 ? 50m : 100m * gain / (gain + loss);
    }

    /// <summary>
    /// Wilder's average true range at the last candle. A candle's true range is the largest of its range,
    /// and the distance from the previous close to its high and to its low, so the first candle has none.
    /// The first average is the simple mean of the first <paramref name="period"/> true ranges, and each
    /// later one is smoothed in with weight 1 / period. Null with fewer than period + 1 candles.
    /// </summary>
    public static decimal? Atr(IReadOnlyList<Candle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        if (candles.Count < period + 1)
        {
            return null;
        }

        decimal atr = 0;

        for (var i = 1; i <= period; i++)
        {
            atr += TrueRange(candles[i], candles[i - 1].Close);
        }

        atr /= period;

        for (var i = period + 1; i < candles.Count; i++)
        {
            atr = ((atr * (period - 1)) + TrueRange(candles[i], candles[i - 1].Close)) / period;
        }

        return atr;
    }

    /// <summary>
    /// The latest swing highs, newest first. A candle is one when its high is above each of the
    /// <paramref name="strength"/> highs before it, and no lower than each of the <paramref name="strength"/>
    /// after it, so a flat top counts once, at its first candle. The last <paramref name="strength"/>
    /// candles can't be confirmed yet.
    /// </summary>
    public static IReadOnlyList<SwingPoint> SwingHighs(IReadOnlyList<Candle> candles, int strength, int count) =>
        Swings(candles, strength, count, candle => candle.High, (candidate, other) => candidate > other, (candidate, other) => candidate >= other);

    /// <summary>The latest swing lows, newest first, defined as for <see cref="SwingHighs"/> with lows.</summary>
    public static IReadOnlyList<SwingPoint> SwingLows(IReadOnlyList<Candle> candles, int strength, int count) =>
        Swings(candles, strength, count, candle => candle.Low, (candidate, other) => candidate < other, (candidate, other) => candidate <= other);

    /// <summary>The last candle's volume against the mean of the last <paramref name="period"/>. Null with fewer candles than that.</summary>
    public static VolumeStatistics? Volume(IReadOnlyList<Candle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        if (candles.Count < period)
        {
            return null;
        }

        decimal total = 0;

        for (var i = candles.Count - period; i < candles.Count; i++)
        {
            total += candles[i].Volume;
        }

        var average = total / period;
        var latest = candles[^1].Volume;

        return new VolumeStatistics(latest, average, average == 0 ? null : latest / average);
    }

    private static List<SwingPoint> Swings(
        IReadOnlyList<Candle> candles,
        int strength,
        int count,
        Func<Candle, decimal> price,
        Func<decimal, decimal, bool> beatsBefore,
        Func<decimal, decimal, bool> holdsAfter)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThan(strength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 0);

        var swings = new List<SwingPoint>();

        for (var i = candles.Count - 1 - strength; i >= strength && swings.Count < count; i--)
        {
            var candidate = price(candles[i]);
            var isSwing = true;

            for (var offset = 1; offset <= strength && isSwing; offset++)
            {
                isSwing = beatsBefore(candidate, price(candles[i - offset])) && holdsAfter(candidate, price(candles[i + offset]));
            }

            if (isSwing)
            {
                swings.Add(new SwingPoint(candles[i].OpenTime, candidate));
            }
        }

        return swings;
    }

    private static decimal TrueRange(Candle candle, decimal previousClose) =>
        Math.Max(candle.High - candle.Low, Math.Max(Math.Abs(candle.High - previousClose), Math.Abs(candle.Low - previousClose)));

    private static decimal Sum(IReadOnlyList<decimal> values, int start, int length)
    {
        decimal total = 0;

        for (var i = start; i < start + length; i++)
        {
            total += values[i];
        }

        return total;
    }
}
