using System.Globalization;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The indicators of a data package are computed by the system, and not by models (FR-CYC-2):
/// moving averages, relative strength, average true range, and volume averages, each held here
/// to known values. See "The indicators" in docs/cycles.md.
/// </summary>
/// <remarks>
/// The known values are of two kinds. A short series is worked out in the comment beside it. A
/// long one is the series of a worked example that is widely used to explain the indicator,
/// with prices to two decimal places, and its values were worked out apart from the code that
/// is tested, from the definition, in exact decimals. Tables that are published for the same
/// examples differ from them by a few hundredths here and there: they were made from prices
/// with more decimal places than they show.
/// </remarks>
public class IndicatorTests
{
    /// <summary>Thirty closes of a worked example of a 10-period simple and exponential moving average.</summary>
    private static readonly decimal[] AveragedCloses = Numbers(
        "22.27 22.19 22.08 22.17 22.18 22.13 22.23 22.43 22.24 22.29 22.15 22.39 22.38 22.61 23.36 " +
        "24.05 23.75 23.83 23.95 23.63 23.82 23.87 23.65 23.19 23.10 23.33 22.68 23.10 22.40 22.17");

    /// <summary>Thirty-three closes of a worked example of a 14-period relative strength index.</summary>
    private static readonly decimal[] StrengthCloses = Numbers(
        "44.34 44.09 44.15 43.61 44.33 44.83 45.10 45.42 45.84 46.08 45.89 46.03 45.61 46.28 46.28 46.00 46.03 " +
        "46.41 46.22 45.64 46.21 46.25 45.71 46.45 45.78 45.35 44.03 44.18 44.22 44.57 43.42 42.66 43.13");

    /// <summary>Thirty candles of a worked example of a 14-period average true range, with a fall of several ranges in it.</summary>
    private static readonly ExchangeCandle[] RangedCandles = Candles(
        highs: "48.70 48.72 48.90 48.87 48.82 49.05 49.20 49.35 49.92 50.19 50.12 49.66 49.88 50.19 50.36 " +
               "50.57 50.65 50.43 49.63 50.33 50.29 50.17 49.32 48.50 48.32 46.80 47.80 48.39 48.66 48.79",
        lows: "47.79 48.14 48.39 48.37 48.24 48.64 48.94 48.86 49.50 49.87 49.20 48.90 49.43 49.73 49.26 " +
              "50.09 50.30 49.21 48.98 49.61 49.20 49.43 48.08 47.64 41.55 44.28 47.31 47.20 47.90 47.73",
        closes: "48.16 48.61 48.75 48.63 48.74 49.03 49.07 49.32 49.91 50.13 49.53 49.50 49.75 50.03 50.31 " +
                "50.52 50.41 49.34 49.37 50.23 49.24 49.93 48.43 48.18 46.57 45.41 47.77 47.72 48.62 47.85");

    [Fact]
    public void A_simple_moving_average_is_the_mean_of_the_latest_values()
    {
        // The latest three of five: (3 + 4 + 5) / 3.
        Assert.Equal(4m, Indicators.SimpleAverage([1m, 2m, 3m, 4m, 5m], 3));

        // All of them, when the period is as long as the series.
        Assert.Equal(3m, Indicators.SimpleAverage([1m, 2m, 3m, 4m, 5m], 5));

        // A value that is no whole number keeps its digits: (0.1 + 0.2) / 2.
        Assert.Equal(0.15m, Indicators.SimpleAverage([7m, 0.1m, 0.2m], 2));
    }

    [Theory]
    [InlineData(10, "22.2210")]
    [InlineData(11, "22.2090")]
    [InlineData(15, "22.4210")]
    [InlineData(20, "23.2100")]
    [InlineData(25, "23.6840")]
    [InlineData(30, "23.1310")]
    public void A_simple_moving_average_has_the_known_values_of_a_worked_example(int closes, string expected) =>
        Assert.Equal(Number(expected), Rounded(Indicators.SimpleAverage(AveragedCloses[..closes], 10)));

    [Fact]
    public void An_exponential_moving_average_starts_as_the_simple_one_and_moves_towards_each_later_value()
    {
        // With a period of 3 the weight is 2 / (3 + 1) = 0.5. It starts as the mean of the first
        // three, (2 + 4 + 6) / 3 = 4. Then 4 + 0.5 * (8 - 4) = 6, and 6 + 0.5 * (10 - 6) = 8.
        Assert.Equal(4m, Indicators.ExponentialAverage([2m, 4m, 6m], 3));
        Assert.Equal(6m, Indicators.ExponentialAverage([2m, 4m, 6m, 8m], 3));
        Assert.Equal(8m, Indicators.ExponentialAverage([2m, 4m, 6m, 8m, 10m], 3));
    }

    [Theory]
    [InlineData(10, "22.2210")]
    [InlineData(11, "22.2081")]
    [InlineData(15, "22.5164")]
    [InlineData(20, "23.3398")]
    [InlineData(25, "23.4036")]
    [InlineData(30, "22.9150")]
    public void An_exponential_moving_average_has_the_known_values_of_a_worked_example(int closes, string expected) =>
        Assert.Equal(Number(expected), Rounded(Indicators.ExponentialAverage(AveragedCloses[..closes], 10)));

    [Fact]
    public void The_relative_strength_index_is_the_share_of_the_gains_in_the_smoothed_movement()
    {
        // With a period of 2. The changes of 10, 11, 10, 12, 11.5 are +1, -1, +2, -0.5.
        // The first two give an average gain of 0.5 and an average loss of 0.5: 50.
        Assert.Equal(50m, Indicators.RelativeStrength([10m, 11m, 10m], 2));

        // +2: the gain is (0.5 + 2) / 2 = 1.25, and the loss (0.5 + 0) / 2 = 0.25.
        // 100 - 100 / (1 + 1.25 / 0.25) = 83.33...
        Assert.Equal(83.3333m, Rounded(Indicators.RelativeStrength([10m, 11m, 10m, 12m], 2)));

        // -0.5: the gain is (1.25 + 0) / 2 = 0.625, and the loss (0.25 + 0.5) / 2 = 0.375.
        // 100 - 100 / (1 + 0.625 / 0.375) = 62.5.
        Assert.Equal(62.5m, Indicators.RelativeStrength([10m, 11m, 10m, 12m, 11.5m], 2));
    }

    [Theory]

    // The first: over the first 14 changes the gains come to 3.34 and the losses to 1.40, so
    // the index is 100 * 3.34 / (3.34 + 1.40) = 70.4641.
    [InlineData(15, "70.4641")]
    [InlineData(16, "66.2496")]
    [InlineData(17, "66.4809")]
    [InlineData(18, "69.3469")]
    [InlineData(19, "66.2947")]
    [InlineData(20, "57.9150")]
    [InlineData(24, "62.3399")]
    [InlineData(27, "40.0194")]
    [InlineData(30, "45.4995")]
    [InlineData(32, "33.0905")]
    [InlineData(33, "37.7888")]
    public void The_relative_strength_index_has_the_known_values_of_a_worked_example(int closes, string expected) =>
        Assert.Equal(Number(expected), Rounded(Indicators.RelativeStrength(StrengthCloses[..closes], 14)));

    [Fact]
    public void The_relative_strength_index_is_100_with_no_loss_0_with_no_gain_and_50_when_nothing_moved()
    {
        Assert.Equal(100m, Indicators.RelativeStrength([1m, 2m, 3m, 4m, 5m], 3));
        Assert.Equal(0m, Indicators.RelativeStrength([5m, 4m, 3m, 2m, 1m], 3));
        Assert.Equal(50m, Indicators.RelativeStrength([7m, 7m, 7m, 7m, 7m], 3));

        // A loss that has all but faded away divides nothing: the index is next to 100, and
        // the working out of it does not fail, as the gain over such a loss would, which is
        // more than a decimal holds.
        decimal[] risingAfterOneLoss = [100m, 99.99999999m, .. Enumerable.Range(1, 496).Select(step => 100m + (step * 1_000_000m))];

        Assert.InRange(Indicators.RelativeStrength(risingAfterOneLoss, 14)!.Value, 99.99m, 100m);
    }

    [Fact]
    public void The_average_true_range_counts_a_gap_from_the_close_before()
    {
        // With a period of 2, as high, low, close.
        ExchangeCandle[] candles =
        [
            // The first has no close before it: 10 - 8 = 2.
            Candle(10m, 8m, 9m),

            // Within the range of the one before: the greatest of 11 - 9, |11 - 9|, |9 - 9| is 2.
            Candle(11m, 9m, 10m),

            // A gap up: the greatest of 14 - 13, |14 - 10|, |13 - 10| is 4.
            Candle(14m, 13m, 13.5m),

            // A gap down: the greatest of 12 - 11, |12 - 13.5|, |11 - 13.5| is 2.5.
            Candle(12m, 11m, 11m),
        ];

        // The mean of the first two true ranges, (2 + 2) / 2.
        Assert.Equal(2m, Indicators.AverageTrueRange(candles[..2], 2));

        // (2 * 1 + 4) / 2.
        Assert.Equal(3m, Indicators.AverageTrueRange(candles[..3], 2));

        // (3 * 1 + 2.5) / 2.
        Assert.Equal(2.75m, Indicators.AverageTrueRange(candles, 2));
    }

    [Theory]
    [InlineData(14, "0.5543")]
    [InlineData(15, "0.5933")]
    [InlineData(16, "0.5852")]
    [InlineData(18, "0.6149")]
    [InlineData(22, "0.6922")]
    [InlineData(24, "0.7810")]

    // The candle that fell from 48.32 to 41.55.
    [InlineData(25, "1.2088")]
    [InlineData(27, "1.3801")]
    [InlineData(30, "1.3163")]
    public void The_average_true_range_has_the_known_values_of_a_worked_example(int candles, string expected) =>
        Assert.Equal(Number(expected), Rounded(Indicators.AverageTrueRange(RangedCandles[..candles], 14)));

    [Fact]
    public void An_indicator_that_has_too_few_candles_is_null()
    {
        decimal[] three = [1m, 2m, 3m];

        // A moving average takes as many values as its period.
        Assert.Null(Indicators.SimpleAverage(three, 4));
        Assert.Null(Indicators.ExponentialAverage(three, 4));
        Assert.NotNull(Indicators.SimpleAverage(three, 3));
        Assert.NotNull(Indicators.ExponentialAverage(three, 3));

        // The relative strength takes as many changes as its period, which is one close more.
        Assert.Null(Indicators.RelativeStrength(three, 3));
        Assert.NotNull(Indicators.RelativeStrength(three, 2));

        // The average true range takes as many candles as its period.
        Assert.Null(Indicators.AverageTrueRange([Candle(2m, 1m, 1.5m)], 2));
        Assert.NotNull(Indicators.AverageTrueRange([Candle(2m, 1m, 1.5m), Candle(2m, 1m, 1.5m)], 2));

        // No series at all has no indicator, and a period is one value at least.
        Assert.Null(Indicators.SimpleAverage([], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Indicators.SimpleAverage(three, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Indicators.ExponentialAverage(three, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Indicators.RelativeStrength(three, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Indicators.AverageTrueRange([], 0));
    }

    [Fact]
    public void The_indicators_of_a_series_are_those_of_its_closes_its_candles_and_its_volumes_with_the_periods_of_their_names()
    {
        // 250 candles that rise and fall, with a volume that differs from candle to candle.
        ExchangeCandle[] candles =
        [
            .. Enumerable.Range(0, 250).Select(step =>
            {
                var close = 100m + (step % 7 * 3m) - (step % 5 * 2m) + (step * 0.25m);

                return new ExchangeCandle(
                    DateTimeOffset.UnixEpoch.AddHours(step),
                    DateTimeOffset.UnixEpoch.AddHours(step + 1),
                    Open: close - 1m,
                    High: close + 2m,
                    Low: close - 3m,
                    Close: close,
                    Volume: 10m + (step % 11));
            }),
        ];

        decimal[] closes = [.. candles.Select(candle => candle.Close)];
        decimal[] volumes = [.. candles.Select(candle => candle.Volume)];

        var indicators = Indicators.Of(candles);

        Assert.Equal(
            new IndicatorValues(
                Sma20: Indicators.SimpleAverage(closes, 20),
                Sma50: Indicators.SimpleAverage(closes, 50),
                Sma200: Indicators.SimpleAverage(closes, 200),
                Ema20: Indicators.ExponentialAverage(closes, 20),
                Ema50: Indicators.ExponentialAverage(closes, 50),
                Rsi14: Indicators.RelativeStrength(closes, 14),
                Atr14: Indicators.AverageTrueRange(candles, 14),
                VolumeAverage20: Indicators.SimpleAverage(volumes, 20)),
            indicators);

        // Every one of them is there, and the volume average is the mean of the latest 20 volumes.
        Assert.DoesNotContain(null, new[]
        {
            indicators.Sma20, indicators.Sma50, indicators.Sma200, indicators.Ema20, indicators.Ema50,
            indicators.Rsi14, indicators.Atr14, indicators.VolumeAverage20,
        });
        Assert.Equal(volumes[^20..].Sum() / 20m, indicators.VolumeAverage20);

        // The periods are those that the names say.
        Assert.Equal(
            (20, 50, 200, 14, 14, 20),
            (Indicators.ShortAverage, Indicators.MediumAverage, Indicators.LongAverage, Indicators.RelativeStrengthPeriod,
                Indicators.TrueRangePeriod, Indicators.VolumeAverage));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(13, 0)]

    // The average true range, with 14 candles.
    [InlineData(14, 1)]

    // The relative strength, with 15 closes.
    [InlineData(15, 2)]
    [InlineData(19, 2)]

    // The averages of 20 candles: the simple one, the exponential one, and that of the volume.
    [InlineData(20, 5)]
    [InlineData(49, 5)]
    [InlineData(50, 7)]
    [InlineData(199, 7)]
    [InlineData(200, 8)]
    public void A_series_has_the_indicators_that_it_has_candles_for_and_no_others(int candles, int indicators)
    {
        var values = Indicators.Of([.. Enumerable.Range(0, candles).Select(step => Candle(11m + step, 9m + step, 10m + step))]);

        Assert.Equal(
            indicators,
            new[] { values.Sma20, values.Sma50, values.Sma200, values.Ema20, values.Ema50, values.Rsi14, values.Atr14, values.VolumeAverage20 }
                .Count(value => value is not null));
    }

    private static decimal Number(string written) => decimal.Parse(written, CultureInfo.InvariantCulture);

    private static decimal[] Numbers(string written) => [.. written.Split(' ').Select(Number)];

    /// <summary>A value to four decimal places, which is what the known values are written to.</summary>
    private static decimal? Rounded(decimal? value) =>
        value is { } known ? Math.Round(known, 4, MidpointRounding.AwayFromZero) : null;

    private static ExchangeCandle Candle(decimal high, decimal low, decimal close) =>
        new(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), Open: close, high, low, close, Volume: 1m);

    private static ExchangeCandle[] Candles(string highs, string lows, string closes) =>
        [.. Numbers(highs).Zip(Numbers(lows), Numbers(closes)).Select(candle => Candle(candle.First, candle.Second, candle.Third))];
}
