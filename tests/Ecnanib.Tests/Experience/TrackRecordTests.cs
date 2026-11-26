using Ecnanib.Experience;

namespace Ecnanib.Tests.Experience;

/// <summary>
/// A track record shows closed trades, win rate, average R, total R, profit factor, largest
/// drawdown in R, and average holding time (FR-EXP-2). See "The measures" in docs/experience.md.
/// </summary>
public class TrackRecordTests
{
    // More than any record of these tests counts, and fewer than none: no record is a small
    // sample unless its test is about that.
    private const int NoMinimum = 1;

    [Fact]
    public void The_win_rate_is_the_wins_among_the_counted_trades_and_a_trade_of_zero_R_is_neither_a_win_nor_a_loss()
    {
        var record = TrackRecord.Of(
            [Trades.Closed(1, 2m), Trades.Closed(2, -1m), Trades.Closed(3, 0m), Trades.Closed(4, 1m)], NoMinimum);

        Assert.Equal((4, 2, 1), (record.Trades, record.Wins, record.Losses));
        Assert.Equal(0.5m, record.WinRate);

        // The total is the sum of the results, and the average is the total over the trades.
        Assert.Equal((2m, 0.5m), (record.TotalR, record.AverageR));
    }

    [Fact]
    public void The_profit_factor_is_the_winning_R_over_the_losing_R_and_is_not_said_without_a_losing_trade()
    {
        // Won 4 R, and lost 2 R.
        Assert.Equal(
            2m,
            TrackRecord.Of([Trades.Closed(1, 3m), Trades.Closed(2, -1.5m), Trades.Closed(3, -0.5m), Trades.Closed(4, 1m)], NoMinimum).ProfitFactor);

        // Nothing was lost, so there is nothing to divide by: a trade of 0 R lost nothing either.
        var noLoss = TrackRecord.Of([Trades.Closed(1, 1m), Trades.Closed(2, 2m), Trades.Closed(3, 0m)], NoMinimum);

        Assert.Null(noLoss.ProfitFactor);
        Assert.Equal((3, 0), (noLoss.Trades, noLoss.Losses));

        // Nothing was won.
        Assert.Equal(0m, TrackRecord.Of([Trades.Closed(1, -1m), Trades.Closed(2, -0.5m)], NoMinimum).ProfitFactor);
    }

    [Fact]
    public void The_largest_drawdown_is_the_deepest_fall_of_the_running_total_from_its_highest_point_starting_at_zero()
    {
        // The total runs 2, 1, -1, 3, 2: it fell by 3 from its height of 2, and later by 1 from 3.
        Assert.Equal(
            3m,
            TrackRecord.Of(
                [Trades.Closed(1, 2m), Trades.Closed(2, -1m), Trades.Closed(3, -2m), Trades.Closed(4, 4m), Trades.Closed(5, -1m)],
                NoMinimum).LargestDrawdownR);

        // The total starts at 0, which is its first height: losses from the start are a fall.
        Assert.Equal(
            2m, TrackRecord.Of([Trades.Closed(1, -1.5m), Trades.Closed(2, -0.5m), Trades.Closed(3, 5m)], NoMinimum).LargestDrawdownR);

        // A total that never falls has no drawdown.
        Assert.Equal(0m, TrackRecord.Of([Trades.Closed(1, 1m), Trades.Closed(2, 0m), Trades.Closed(3, 2m)], NoMinimum).LargestDrawdownR);
    }

    [Fact]
    public void The_drawdown_goes_by_the_order_the_trades_closed_in_however_they_are_handed_over()
    {
        var first = Trades.Closed(1, 2m);
        var second = Trades.Closed(2, -3m);
        var third = Trades.Closed(3, 2m);

        // Closed in this order the total fell by 3. In another order it would have fallen by
        // less, or by more: the order they are handed over in says nothing.
        Assert.Equal(3m, TrackRecord.Of([first, second, third], NoMinimum).LargestDrawdownR);
        Assert.Equal(3m, TrackRecord.Of([third, second, first], NoMinimum).LargestDrawdownR);
        Assert.Equal(3m, TrackRecord.Of([second, third, first], NoMinimum).LargestDrawdownR);

        // Two trades of one moment are told apart by their ids, so the same trades always give
        // the same record.
        var earlier = Trades.Closed(5, 1m);

        // The id of a later moment, said outright: two ids that are made within one millisecond
        // come in either order, and the test would then hold or fail by chance.
        var later = Trades.Closed(5, -1m) with { PositionId = Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(1)) };

        Assert.True(earlier.PositionId < later.PositionId);
        Assert.Equal(1m, TrackRecord.Of([later, earlier], NoMinimum).LargestDrawdownR);
        Assert.Equal(1m, TrackRecord.Of([earlier, later], NoMinimum).LargestDrawdownR);
    }

    [Fact]
    public void The_average_holding_time_is_the_mean_of_the_holding_times()
    {
        var record = TrackRecord.Of(
            [Trades.Closed(1, 1m, holdingSeconds: 1800), Trades.Closed(2, -1m, holdingSeconds: 5400), Trades.Closed(3, 1m, holdingSeconds: 7200)],
            NoMinimum);

        Assert.Equal(TimeSpan.FromMinutes(80), record.AverageHoldingTime);
    }

    [Fact]
    public void A_record_of_no_trades_says_no_rate_no_average_and_no_factor()
    {
        var record = TrackRecord.Of([], NoMinimum);

        Assert.Equal((0, 0, 0, 0), (record.Trades, record.Wins, record.Losses, record.Estimates));
        Assert.Null(record.WinRate);
        Assert.Null(record.AverageR);
        Assert.Null(record.ProfitFactor);
        Assert.Null(record.AverageHoldingTime);

        // Nothing was made and nothing was lost.
        Assert.Equal((0m, 0m), (record.TotalR, record.LargestDrawdownR));
    }

    [Fact]
    public void A_result_that_is_an_estimate_is_counted_and_said_to_be_one()
    {
        // A position that was liquidated has a result that is an estimate. Leaving it out would
        // leave out the worst trade of the record.
        var record = TrackRecord.Of(
            [Trades.Closed(1, 1m), Trades.Closed(2, -4m, complete: false), Trades.Closed(3, 1m, complete: false)], NoMinimum);

        Assert.Equal((3, 2), (record.Trades, record.Estimates));
        Assert.Equal((-2m, 4m), (record.TotalR, record.LargestDrawdownR));
    }

    [Fact]
    public void A_trade_without_a_result_in_R_is_in_no_figure()
    {
        var counted = TrackRecord.Of([Trades.Closed(1, 2m, holdingSeconds: 600), Trades.Closed(4, -1m, holdingSeconds: 600)], NoMinimum);

        // One whose result is not recorded yet, and one whose result says no R: every measure is
        // of the same trades, so neither is in the count, in the holding time, or in the estimates.
        var record = TrackRecord.Of(
            [
                Trades.Closed(1, 2m, holdingSeconds: 600),
                Trades.Awaiting(2),
                Trades.Closed(3, null, holdingSeconds: 86_400, complete: false),
                Trades.Closed(4, -1m, holdingSeconds: 600),
            ],
            NoMinimum);

        Assert.Equal(counted, record);
        Assert.Equal((2, 0, TimeSpan.FromMinutes(10)), (record.Trades, record.Estimates, record.AverageHoldingTime));
    }

    [Fact]
    public void A_record_below_the_minimum_sample_is_marked_as_a_small_sample()
    {
        Assert.True(TrackRecord.Of([Trades.Closed(1, 1m), Trades.Closed(2, 1m)], minSamples: 3).SmallSample);
        Assert.False(TrackRecord.Of([Trades.Closed(1, 1m), Trades.Closed(2, 1m), Trades.Closed(3, 1m)], minSamples: 3).SmallSample);

        // A trade that is in no figure is in no sample either.
        Assert.True(TrackRecord.Of([Trades.Closed(1, 1m), Trades.Closed(2, 1m), Trades.Awaiting(3)], minSamples: 3).SmallSample);
        Assert.True(TrackRecord.Of([], minSamples: 3).SmallSample);
    }

    [Fact]
    public void Results_that_are_out_of_the_range_of_a_number_say_no_sum_and_fail_nothing()
    {
        // A trade on which next to nothing was risked has a result of very many R.
        var record = TrackRecord.Of([Trades.Closed(1, decimal.MaxValue), Trades.Closed(2, decimal.MaxValue)], NoMinimum);

        Assert.Equal((2, 2, 0, 1m), (record.Trades, record.Wins, record.Losses, record.WinRate));
        Assert.Null(record.TotalR);
        Assert.Null(record.AverageR);
        Assert.Null(record.ProfitFactor);
        Assert.Null(record.LargestDrawdownR);
        Assert.Equal(TimeSpan.FromHours(1), record.AverageHoldingTime);
    }
}
