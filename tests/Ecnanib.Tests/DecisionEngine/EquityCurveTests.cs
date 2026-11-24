using Ecnanib.DecisionEngine;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The agent screen shows an equity curve, and the largest drawdown among its performance
/// figures (FR-RPT-2). This is the one rule for what the recorded equity of an agent says: its
/// largest drawdown, counted as the drawdown limit counts a drawdown (FR-RSK-8), and what of a
/// long series is drawn. See docs/agent-screen.md.
/// </summary>
/// <remarks>
/// The agent of these tests has a capital of 1000, unless a test says that its user changed it.
/// The recordings are an hour apart, from nine in the morning of a day, UTC.
/// </remarks>
public class EquityCurveTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_equity_that_was_never_recorded_has_no_curve_and_no_drawdown()
    {
        var curve = EquityCurves.Of([]);

        Assert.Equal(EquityCurve.None, curve);
        Assert.Equal(0, curve.Recorded);
        Assert.Empty(curve.Points);
        Assert.Null(curve.First);
        Assert.Null(curve.LargestDrawdown);
    }

    [Fact]
    public void A_short_series_is_drawn_whole_and_says_its_first_its_latest_its_lowest_and_its_highest()
    {
        var recorded = Series(1000m, 1010m, 990m, 1030m, 1020m);

        var curve = EquityCurves.Of(recorded);

        Assert.Equal(5, curve.Recorded);
        Assert.Equal(recorded, curve.Points);
        Assert.Equal((recorded[0], recorded[4], recorded[2], recorded[3]), (curve.First, curve.Latest, curve.Lowest, curve.Highest));

        // One recording is a series too: it is its own first, latest, lowest, and highest.
        var once = EquityCurves.Of(Series(1000m));

        Assert.Equal((1, once.First, once.First, once.First), (once.Recorded, once.Latest, once.Lowest, once.Highest));
        Assert.Null(once.LargestDrawdown);
    }

    [Fact]
    public void The_largest_drawdown_is_the_deepest_fall_from_a_peak_in_percent_of_the_peak()
    {
        // Up to 1100, down to 990, up to 1200, down to 1140: a fall of 10 percent, and then one of 5.
        var recorded = Series(1000m, 1100m, 1045m, 990m, 1200m, 1140m);

        var drawdown = EquityCurves.LargestDrawdown(recorded);

        Assert.NotNull(drawdown);
        Assert.Equal((10m, 110m), (drawdown.Percent, drawdown.Amount));
        Assert.Equal((Morning.AddHours(1), Morning.AddHours(3)), (drawdown.PeakAt, drawdown.LowAt));

        // The same by the curve.
        Assert.Equal(drawdown, EquityCurves.Of(recorded).LargestDrawdown);
    }

    [Fact]
    public void An_agent_that_only_lost_fell_from_the_capital_that_it_began_with()
    {
        // Its first recording is below its capital already: the peak is the capital, at no moment that was recorded.
        var drawdown = EquityCurves.LargestDrawdown(Series(980m, 950m, 970m));

        Assert.NotNull(drawdown);
        Assert.Equal((5m, 50m, null, Morning.AddHours(1)), (drawdown.Percent, drawdown.Amount, drawdown.PeakAt, drawdown.LowAt));
    }

    [Fact]
    public void An_equity_that_never_fell_from_a_peak_has_no_drawdown()
    {
        Assert.Null(EquityCurves.LargestDrawdown(Series(1000m, 1000m, 1010m, 1010m, 1025m)));
    }

    [Fact]
    public void A_capital_that_its_user_changed_makes_no_drawdown_and_takes_none_away()
    {
        // A capital of 1000, a peak of 1100, back to 1000: a fall of 100 from 1100, which is 9.09 percent.
        // Then its user lowers the capital to 100, with the result unchanged at nothing: the equity is 100.
        EquityPoint[] lowered =
        [
            new(Morning, 1000m, 1000m),
            new(Morning.AddHours(1), 1100m, 1000m),
            new(Morning.AddHours(2), 1000m, 1000m),
            new(Morning.AddHours(3), 100m, 100m),
        ];

        var drawdown = EquityCurves.LargestDrawdown(lowered);

        // The fall is the same share of the peak as before the change, and not the 50 percent
        // that a peak of 100 above a capital of 100 would make of it.
        Assert.NotNull(drawdown);
        Assert.Equal(9.09m, Math.Round(drawdown.Percent, 2));
        Assert.Equal(Morning.AddHours(1), drawdown.PeakAt);

        // A capital that is raised makes no fall smaller either: 1000 to 950 is 5 percent, and stays 5 percent under 10000.
        EquityPoint[] raised =
        [
            new(Morning, 1000m, 1000m),
            new(Morning.AddHours(1), 950m, 1000m),
            new(Morning.AddHours(2), 9950m, 10000m),
        ];

        Assert.Equal(5m, Math.Round(EquityCurves.LargestDrawdown(raised)!.Percent, 2));

        // And an equity that rises because more was allocated is no new peak: the fall after it is counted from the old one.
        EquityPoint[] more =
        [
            new(Morning, 1000m, 1000m),
            new(Morning.AddHours(1), 2000m, 2000m),
            new(Morning.AddHours(2), 1900m, 2000m),
        ];

        Assert.Equal(5m, EquityCurves.LargestDrawdown(more)!.Percent);
    }

    [Fact]
    public void A_long_series_is_drawn_with_few_points_and_loses_no_peak_and_no_fall()
    {
        // A thousand recordings a quarter of an hour apart, which rise slowly, with one spike and one dip.
        var recorded = Enumerable.Range(0, 1000)
            .Select(index => new EquityPoint(
                Morning.AddMinutes(15 * index),
                index switch
                {
                    333 => 5000m,
                    666 => 10m,
                    _ => 1000m + index,
                },
                Capital: 1000m))
            .ToList();

        var curve = EquityCurves.Of(recorded, mostPoints: 40);

        Assert.Equal(1000, curve.Recorded);
        Assert.InRange(curve.Points.Count, 2, 40);

        // In the order of time, each recording once, from the first to the last.
        Assert.Equal(curve.Points.OrderBy(point => point.At), curve.Points);
        Assert.Equal(curve.Points.Count, curve.Points.Distinct().Count());
        Assert.Equal((recorded[0], recorded[^1]), (curve.Points[0], curve.Points[^1]));

        // The spike and the dip are on the line, and are what the curve says is its highest and its lowest.
        Assert.Contains(recorded[333], curve.Points);
        Assert.Contains(recorded[666], curve.Points);
        Assert.Equal((recorded[333], recorded[666]), (curve.Highest, curve.Lowest));

        // The drawdown is counted over every recording, not over what is drawn.
        Assert.Equal(EquityCurves.LargestDrawdown(recorded), curve.LargestDrawdown);

        // A series of no more recordings than are drawn is drawn whole.
        Assert.Equal(40, EquityCurves.Of(recorded.Take(40).ToList(), mostPoints: 40).Points.Count);
    }

    [Fact]
    public void Numbers_that_cannot_be_worked_out_with_say_nothing_and_fail_nothing()
    {
        EquityPoint[] recorded =
        [
            new(Morning, decimal.MaxValue, 1000m),
            new(Morning.AddHours(1), decimal.MinValue, decimal.MaxValue),
            new(Morning.AddHours(2), 900m, 1000m),
        ];

        // Nothing is thrown, and the curve still says its recordings.
        var curve = EquityCurves.Of(recorded);

        Assert.Equal(3, curve.Recorded);
    }

    /// <summary>Recordings an hour apart from the morning, under a capital of 1000.</summary>
    private static EquityPoint[] Series(params decimal[] equities) =>
        [.. equities.Select((equity, hour) => new EquityPoint(Morning.AddHours(hour), equity, Capital: 1000m))];
}
