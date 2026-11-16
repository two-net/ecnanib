using Ecnanib.ExchangeGateway;

namespace Ecnanib.DecisionEngine;

/// <summary>
/// The indicators of a data package: moving averages, relative strength, average true range, and
/// volume averages. They are computed by the system, and not by models (FR-CYC-2): a model is
/// handed the numbers, and works none of them out. See "The indicators" in docs/cycles.md.
/// </summary>
/// <remarks>
/// <para>
/// Each is a function of closed candles, oldest first, and of nothing else: the same candles
/// give the same numbers. Everything is a decimal, as the candles are, so no digit of a price is
/// lost on the way.
/// </para>
/// <para>
/// An indicator that has too few candles is null, and never a number that stands for one. The
/// periods are fixed, and are no settings.
/// </para>
/// </remarks>
public static class Indicators
{
    /// <summary>The candles of the short moving averages, the simple one and the exponential one.</summary>
    public const int ShortAverage = 20;

    /// <summary>The candles of the medium moving averages, the simple one and the exponential one.</summary>
    public const int MediumAverage = 50;

    /// <summary>The candles of the long simple moving average.</summary>
    public const int LongAverage = 200;

    /// <summary>The period of the relative strength index.</summary>
    public const int RelativeStrengthPeriod = 14;

    /// <summary>The period of the average true range.</summary>
    public const int TrueRangePeriod = 14;

    /// <summary>The candles of the volume average.</summary>
    public const int VolumeAverage = 20;

    /// <summary>
    /// The indicators of a series of candles, at its latest close, each with the period that
    /// its name says.
    /// </summary>
    /// <param name="candles">Closed candles of one length, oldest first.</param>
    public static IndicatorValues Of(IReadOnlyList<ExchangeCandle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        decimal[] closes = [.. candles.Select(candle => candle.Close)];
        decimal[] volumes = [.. candles.Select(candle => candle.Volume)];

        return new IndicatorValues(
            Sma20: SimpleAverage(closes, ShortAverage),
            Sma50: SimpleAverage(closes, MediumAverage),
            Sma200: SimpleAverage(closes, LongAverage),
            Ema20: ExponentialAverage(closes, ShortAverage),
            Ema50: ExponentialAverage(closes, MediumAverage),
            Rsi14: RelativeStrength(closes, RelativeStrengthPeriod),
            Atr14: AverageTrueRange(candles, TrueRangePeriod),
            VolumeAverage20: SimpleAverage(volumes, VolumeAverage));
    }

    /// <summary>The simple moving average: the mean of the latest <paramref name="period"/> values.</summary>
    /// <param name="values">The values, oldest first.</param>
    /// <param name="period">How many values the average is of.</param>
    /// <returns>The average at the latest value, or null when there are fewer values than the period.</returns>
    public static decimal? SimpleAverage(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        if (values.Count < period)
        {
            return null;
        }

        var sum = 0m;

        for (var index = values.Count - period; index < values.Count; index++)
        {
            sum += values[index];
        }

        return sum / period;
    }

    /// <summary>
    /// The exponential moving average, which weighs a later value more: it starts as the simple
    /// average of the first <paramref name="period"/> values, and moves towards each value after
    /// them by <c>2 / (period + 1)</c> of what it differs by.
    /// </summary>
    /// <param name="values">The values, oldest first. Every one of them counts: more of them make the average more exact.</param>
    /// <param name="period">The period of the average.</param>
    /// <returns>The average at the latest value, or null when there are fewer values than the period.</returns>
    public static decimal? ExponentialAverage(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        if (values.Count < period)
        {
            return null;
        }

        var average = 0m;

        for (var index = 0; index < period; index++)
        {
            average += values[index];
        }

        average /= period;

        var weight = 2m / (period + 1);

        for (var index = period; index < values.Count; index++)
        {
            average += weight * (values[index] - average);
        }

        return average;
    }

    /// <summary>
    /// The relative strength index of Wilder, from 0 to 100: how much of the recent movement of
    /// the close was upward. The average gain and the average loss start as the means of the
    /// first <paramref name="period"/> changes, and each change after them counts for one part
    /// in <paramref name="period"/>.
    /// </summary>
    /// <param name="closes">The closes, oldest first.</param>
    /// <param name="period">The period of the index.</param>
    /// <returns>
    /// The index at the latest close, or null when there are fewer changes than the period, which
    /// takes one close more than the period. With no loss it is 100, with no gain 0, and 50 when
    /// the close did not move at all.
    /// </returns>
    public static decimal? RelativeStrength(IReadOnlyList<decimal> closes, int period)
    {
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        if (closes.Count <= period)
        {
            return null;
        }

        var gain = 0m;
        var loss = 0m;

        for (var index = 1; index <= period; index++)
        {
            var change = closes[index] - closes[index - 1];

            gain += Math.Max(change, 0m);
            loss += Math.Max(-change, 0m);
        }

        gain /= period;
        loss /= period;

        for (var index = period + 1; index < closes.Count; index++)
        {
            var change = closes[index] - closes[index - 1];

            gain = ((gain * (period - 1)) + Math.Max(change, 0m)) / period;
            loss = ((loss * (period - 1)) + Math.Max(-change, 0m)) / period;
        }

        // 100 - 100 / (1 + gain / loss), written so that a loss that is next to nothing divides
        // nothing: the share of the gain in all of the movement.
        var movement = gain + loss;

        return movement == 0m ? 50m : 100m * gain / movement;
    }

    /// <summary>
    /// The average true range of Wilder: how far the price moves in one candle, gaps from the
    /// close before it included. The true range of a candle is the greatest of its high less its
    /// low, and how far its high and its low are from the close before it. The average starts as
    /// the mean of the first <paramref name="period"/> true ranges, and each one after them counts
    /// for one part in <paramref name="period"/>.
    /// </summary>
    /// <param name="candles">The candles, oldest first. The first has no close before it: its true range is its high less its low.</param>
    /// <param name="period">The period of the average.</param>
    /// <returns>The average at the latest candle, in the asset that the symbol is priced in, or null when there are fewer candles than the period.</returns>
    public static decimal? AverageTrueRange(IReadOnlyList<ExchangeCandle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);

        if (candles.Count < period)
        {
            return null;
        }

        var average = 0m;

        for (var index = 0; index < period; index++)
        {
            average += TrueRange(candles, index);
        }

        average /= period;

        for (var index = period; index < candles.Count; index++)
        {
            average = ((average * (period - 1)) + TrueRange(candles, index)) / period;
        }

        return average;
    }

    private static decimal TrueRange(IReadOnlyList<ExchangeCandle> candles, int index)
    {
        var candle = candles[index];
        var range = candle.High - candle.Low;

        if (index == 0)
        {
            return range;
        }

        var before = candles[index - 1].Close;

        return Math.Max(range, Math.Max(Math.Abs(candle.High - before), Math.Abs(candle.Low - before)));
    }
}

/// <summary>
/// The indicators of one series of candles, at its latest close. Each is null when the series
/// has too few candles for it: a symbol that is new to the exchange has no average of 200 candles.
/// </summary>
/// <param name="Sma20">The simple moving average of the close over 20 candles.</param>
/// <param name="Sma50">The simple moving average of the close over 50 candles.</param>
/// <param name="Sma200">The simple moving average of the close over 200 candles.</param>
/// <param name="Ema20">The exponential moving average of the close, with a period of 20.</param>
/// <param name="Ema50">The exponential moving average of the close, with a period of 50.</param>
/// <param name="Rsi14">The relative strength index of the close, with a period of 14, from 0 to 100.</param>
/// <param name="Atr14">The average true range, with a period of 14, in the asset that the symbol is priced in.</param>
/// <param name="VolumeAverage20">The mean volume of 20 candles, in the asset that is traded.</param>
public sealed record IndicatorValues(
    decimal? Sma20,
    decimal? Sma50,
    decimal? Sma200,
    decimal? Ema20,
    decimal? Ema50,
    decimal? Rsi14,
    decimal? Atr14,
    decimal? VolumeAverage20);
