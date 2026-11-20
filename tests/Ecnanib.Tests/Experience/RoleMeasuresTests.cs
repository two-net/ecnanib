using Ecnanib.Experience;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// A role-specific measure is kept for each role. For the two analyst roles: the share of
/// non-neutral calls whose direction matched the price change over the next 4 closed candles,
/// minus 50%. For the Strategist: the average R of executed plans. For the Risk Manager: the
/// average R of approved plans minus the average R that vetoed plans would have produced
/// (FR-EXP-3). See "The measure of each role" in docs/experience.md.
/// </summary>
public class RoleMeasuresTests
{
    [Theory]

    // 13 of 20 matched: 65 percent, which is 15 points above a half.
    [InlineData(13, 7, "0.15")]

    // Right half of the time is at zero, which is what a coin is at.
    [InlineData(10, 10, "0")]

    // Right less often than a coin is below zero.
    [InlineData(1, 3, "-0.25")]

    // Always right, and never.
    [InlineData(8, 0, "0.5")]
    [InlineData(0, 8, "-0.5")]
    public void The_measure_of_an_analyst_is_the_share_of_matched_calls_minus_a_half(int matched, int missed, string measure)
    {
        var worked = RoleMeasures.OfAnalyst(matched, missed);

        Assert.Equal(decimal.Parse(measure), worked.Value);

        // Its sample is every call that was scored, matched or not. A neutral call is not among them.
        Assert.Equal(matched + missed, worked.Samples);
        Assert.Null(worked.Reviewed);
    }

    [Fact]
    public void An_analyst_with_no_scored_call_has_no_measure()
    {
        Assert.Equal(new RoleMeasure(null, 0), RoleMeasures.OfAnalyst(0, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => RoleMeasures.OfAnalyst(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleMeasures.OfAnalyst(0, -1));
    }

    [Fact]
    public void The_measure_of_the_Strategist_is_the_average_R_of_its_executed_plans()
    {
        // 2 R, -1 R, 0.5 R, and -1 R: 0.5 R over 4 plans.
        Assert.Equal(new RoleMeasure(0.125m, 4), RoleMeasures.OfStrategist([2m, -1m, 0.5m, -1m]));

        // One plan is its own average.
        Assert.Equal(new RoleMeasure(-1.07m, 1), RoleMeasures.OfStrategist([-1.07m]));

        // No executed plan, and no measure.
        Assert.Equal(new RoleMeasure(null, 0), RoleMeasures.OfStrategist([]));
    }

    [Fact]
    public void The_measure_of_the_Risk_Manager_is_what_it_approved_minus_what_it_vetoed()
    {
        // It approved plans that made 0.5 R on average, and vetoed plans that would have lost
        // 0.75 R on average: it kept 1.25 R out for each plan that it let through.
        var careful = RoleMeasures.OfRiskManager(approvedInR: [1.5m, -0.5m], vetoedInR: [-1m, -1m, -0.25m]);

        Assert.Equal(1.25m, careful.Value);

        // Its sample is every plan that it reviewed and that has a result, on either side.
        Assert.Equal(5, careful.Samples);
        Assert.Equal(new ReviewedPlans(Approved: 2, Vetoed: 3), careful.Reviewed);

        // One that vetoes the plans which would have won is below zero.
        Assert.Equal(-2.5m, RoleMeasures.OfRiskManager(approvedInR: [-1m], vetoedInR: [1m, 2m]).Value);

        // One whose vetoed plans would have done as its approved ones did added nothing.
        Assert.Equal(0m, RoleMeasures.OfRiskManager(approvedInR: [0.4m], vetoedInR: [0.4m]).Value);
    }

    [Fact]
    public void The_measure_of_the_Risk_Manager_is_not_said_while_one_side_has_no_result()
    {
        // It vetoed nothing that has a result: there is nothing to take away, and what is left
        // would be the measure of the Strategist. The sample is counted all the same.
        var neverVetoed = RoleMeasures.OfRiskManager(approvedInR: [1m, 2m, -1m], vetoedInR: []);

        Assert.Null(neverVetoed.Value);
        Assert.Equal(3, neverVetoed.Samples);
        Assert.Equal(new ReviewedPlans(Approved: 3, Vetoed: 0), neverVetoed.Reviewed);

        // It approved nothing that has a result.
        var onlyVetoed = RoleMeasures.OfRiskManager(approvedInR: [], vetoedInR: [-1m, 0.5m]);

        Assert.Null(onlyVetoed.Value);
        Assert.Equal(2, onlyVetoed.Samples);

        // And it reviewed nothing at all.
        Assert.Equal(new RoleMeasure(null, 0, new ReviewedPlans(0, 0)), RoleMeasures.OfRiskManager([], []));
    }

    [Fact]
    public void Results_that_are_out_of_the_range_of_a_number_say_no_measure_and_fail_nothing()
    {
        // A trade on which next to nothing was risked has a result of very many R, and two of
        // them cannot be added up. The sample is counted, and the measure is not said.
        Assert.Equal(new RoleMeasure(null, 2), RoleMeasures.OfStrategist([decimal.MaxValue, decimal.MaxValue]));

        var reviewed = RoleMeasures.OfRiskManager(approvedInR: [decimal.MaxValue, decimal.MaxValue], vetoedInR: [1m]);

        Assert.Null(reviewed.Value);
        Assert.Equal(3, reviewed.Samples);

        // Each side can be said, and what is between them cannot.
        Assert.Null(RoleMeasures.OfRiskManager(approvedInR: [decimal.MaxValue], vetoedInR: [decimal.MinValue]).Value);
    }
}
