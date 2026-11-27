using Ecnanib.Platform.Availability;

namespace Ecnanib.Tests.Reliability;

/// <summary>
/// Target availability is 99.5% per month, excluding announced maintenance (NFR-REL-1). The
/// availability of a month is the share of it that the service was up for, of the time that it
/// was to be up for: what was measured of the month, without the maintenance that was announced
/// in advance. These tests are of that arithmetic alone, which reads nothing. See "Availability"
/// in docs/reliability.md.
/// </summary>
public class AvailabilityFigureTests
{
    private const decimal Target = 99.5m;

    // A month of 30 days, which is 720 hours: 99.5% of it leaves 3 hours and 36 minutes.
    private static readonly DateOnly November = new(2026, 11, 1);

    private static readonly DateTimeOffset Before = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset After = new(2026, 12, 5, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_month_that_the_service_was_up_for_all_of_is_at_a_hundred_percent()
    {
        var month = MonthAvailability.Of(November, After, Before, [new Stretch(Before, After)], [], Target);

        Assert.Equal(
            new MonthAvailability(
                November,
                MeasuredFrom: At(1),
                MeasuredUntil: At(1).AddMonths(1),
                Measured: TimeSpan.FromDays(30),
                Down: TimeSpan.Zero,
                Maintenance: TimeSpan.Zero,
                Percent: 100m,
                MeetsTarget: true),
            month);
    }

    [Theory]

    // Three hours of 720: 99.58%, which is the target and more.
    [InlineData(3, true)]

    // Four hours: 99.44%, which is not.
    [InlineData(4, false)]
    public void The_time_that_nothing_said_the_service_was_up_for_counts_as_down_and_is_held_against_the_target(int hours, bool meets)
    {
        var month = MonthAvailability.Of(
            November,
            After,
            Before,
            [new Stretch(Before, At(10, 12)), new Stretch(At(10, 12 + hours), After)],
            [],
            Target);

        Assert.Equal(TimeSpan.FromHours(720), month.Measured);
        Assert.Equal(TimeSpan.FromHours(hours), month.Down);
        Assert.Equal(100m * (720 - hours) / 720, month.Percent);
        Assert.Equal(meets, month.MeetsTarget);
    }

    [Fact]
    public void A_month_is_exactly_at_its_target_when_it_was_down_for_what_the_target_leaves()
    {
        // 3 hours and 36 minutes of 720 hours are half a percent.
        var month = MonthAvailability.Of(
            November, After, Before, [new Stretch(Before, At(10, 12)), new Stretch(At(10, 15, 36), After)], [], Target);

        Assert.Equal(99.5m, month.Percent);
        Assert.True(month.MeetsTarget);

        // And a second more is under it.
        var under = MonthAvailability.Of(
            November, After, Before, [new Stretch(Before, At(10, 12)), new Stretch(At(10, 15, 36).AddSeconds(1), After)], [], Target);

        Assert.True(under.Percent < 99.5m);
        Assert.False(under.MeetsTarget);
    }

    [Fact]
    public void A_month_that_the_measure_began_in_is_held_to_what_was_measured_of_it()
    {
        // The service first said that it was up on the 16th: the days before are no downtime.
        var month = MonthAvailability.Of(November, After, At(16), [new Stretch(At(16), After)], [], Target);

        Assert.Equal((At(16), At(1).AddMonths(1)), (month.MeasuredFrom, month.MeasuredUntil));
        Assert.Equal(TimeSpan.FromDays(15), month.Measured);
        Assert.Equal((TimeSpan.Zero, 100m, true), (month.Down, month.Percent, month.MeetsTarget));
    }

    [Fact]
    public void A_month_that_still_runs_is_measured_until_now()
    {
        var now = At(20, 12);
        var month = MonthAvailability.Of(November, now, Before, [new Stretch(Before, At(20, 6))], [], Target);

        Assert.Equal((At(1), now), (month.MeasuredFrom, month.MeasuredUntil));
        Assert.Equal(TimeSpan.FromHours((19 * 24) + 12), month.Measured);
        Assert.Equal(TimeSpan.FromHours(6), month.Down);
    }

    [Fact]
    public void Maintenance_that_was_announced_in_advance_is_left_out_of_the_month()
    {
        // Down for three hours, of which the first two were announced: one hour counts, of
        // the 718 that the service was to be up for.
        var month = MonthAvailability.Of(
            November,
            After,
            Before,
            [new Stretch(Before, At(10, 12)), new Stretch(At(10, 15), After)],
            [new Stretch(At(10, 12), At(10, 14))],
            Target);

        Assert.Equal(TimeSpan.FromHours(2), month.Maintenance);
        Assert.Equal(TimeSpan.FromHours(718), month.Measured);
        Assert.Equal(TimeSpan.FromHours(1), month.Down);
        Assert.Equal(100m * 717 / 718, month.Percent);
        Assert.True(month.MeetsTarget);
    }

    [Fact]
    public void Maintenance_is_left_out_whether_the_service_was_down_during_it_or_not()
    {
        // The maintenance took no downtime at all: its two hours are no time that the service
        // was to be up for, and no time that it is given for being up either.
        var month = MonthAvailability.Of(November, After, Before, [new Stretch(Before, After)], [new Stretch(At(5), At(5, 2))], Target);

        Assert.Equal(TimeSpan.FromHours(2), month.Maintenance);
        Assert.Equal(TimeSpan.FromHours(718), month.Measured);
        Assert.Equal((TimeSpan.Zero, 100m), (month.Down, month.Percent));
    }

    [Fact]
    public void Maintenance_that_overlaps_is_left_out_once_and_only_as_far_as_it_lies_in_the_month()
    {
        var month = MonthAvailability.Of(
            November,
            After,
            Before,
            [new Stretch(Before, After)],
            [
                // Two that overlap: three hours together, and not four.
                new Stretch(At(5), At(5, 2)),
                new Stretch(At(5, 1), At(5, 3)),

                // One that began in October, and one that ends in December: an hour each is of November.
                new Stretch(At(1).AddHours(-5), At(1, 1)),
                new Stretch(At(30, 23), At(30, 23).AddHours(6)),
            ],
            Target);

        Assert.Equal(TimeSpan.FromHours(5), month.Maintenance);
        Assert.Equal(TimeSpan.FromHours(715), month.Measured);
    }

    [Fact]
    public void Two_instances_that_were_up_at_the_same_time_are_counted_once()
    {
        var month = MonthAvailability.Of(
            November,
            After,
            Before,
            [new Stretch(Before, At(10, 12)), new Stretch(At(10, 11), At(20)), new Stretch(At(20), After)],
            [],
            Target);

        Assert.Equal((TimeSpan.FromHours(720), TimeSpan.Zero, 100m), (month.Measured, month.Down, month.Percent));
    }

    [Fact]
    public void A_month_that_nothing_was_measured_of_is_not_known_and_is_no_figure()
    {
        var nothing = new MonthAvailability(November, null, null, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null, null);

        // The service never said that it was up.
        Assert.Equal(nothing, MonthAvailability.Of(November, After, firstUp: null, [], [], Target));

        // It first said so after the month was over.
        Assert.Equal(nothing, MonthAvailability.Of(November, After, After.AddDays(-1), [new Stretch(After.AddDays(-1), After)], [], Target));

        // The month has not begun.
        Assert.Equal(nothing, MonthAvailability.Of(November, Before, Before.AddDays(-1), [new Stretch(Before.AddDays(-1), Before)], [], Target));
    }

    [Fact]
    public void A_month_of_which_all_that_was_measured_was_announced_maintenance_is_not_known_either()
    {
        // Measured from the 30th at 20:00, and maintenance from then to the end of the month.
        var month = MonthAvailability.Of(November, After, At(30, 20), [], [new Stretch(At(30, 20), After)], Target);

        Assert.Equal((At(30, 20), At(1).AddMonths(1)), (month.MeasuredFrom, month.MeasuredUntil));
        Assert.Equal((TimeSpan.Zero, TimeSpan.FromHours(4)), (month.Measured, month.Maintenance));
        Assert.Null(month.Percent);
        Assert.Null(month.MeetsTarget);
    }

    private static DateTimeOffset At(int day, int hour = 0, int minute = 0) => new(2026, 11, day, hour, minute, 0, TimeSpan.Zero);
}
