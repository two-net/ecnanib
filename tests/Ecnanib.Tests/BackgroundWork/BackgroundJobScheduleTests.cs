using System.Globalization;
using Ecnanib.Platform.BackgroundWork;

namespace Ecnanib.Tests.BackgroundWork;

/// <summary>
/// When a job is next due, from when it last ran. An interval goes on from the previous run, so
/// it survives a restart (NFR-ARC-4). UTC boundaries that have passed are skipped (FR-CYC-8).
/// </summary>
public class BackgroundJobScheduleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_job_that_has_never_run_is_due_at_once()
    {
        var schedule = BackgroundJobSchedule.Every(TimeSpan.FromHours(24));

        Assert.Equal(Noon, schedule.NextRun(previousStart: null, now: Noon));
    }

    [Fact]
    public void An_interval_is_measured_from_the_start_of_the_previous_run()
    {
        var schedule = BackgroundJobSchedule.Every(TimeSpan.FromHours(24));

        // Whenever it is asked: an hour after the run, or a restart later.
        Assert.Equal(Noon.AddHours(24), schedule.NextRun(Noon, now: Noon.AddHours(1)));
        Assert.Equal(Noon.AddHours(24), schedule.NextRun(Noon, now: Noon.AddHours(23)));
    }

    [Fact]
    public void An_overdue_interval_is_due_once_however_many_intervals_were_missed()
    {
        var schedule = BackgroundJobSchedule.Every(TimeSpan.FromHours(24));
        var now = Noon.AddDays(5);

        // Due since the first interval ended, which is in the past: at once.
        var due = schedule.NextRun(Noon, now);
        Assert.Equal(Noon.AddHours(24), due);
        Assert.True(due <= now);

        // And after that one run, not again before a whole interval has passed.
        Assert.Equal(now.AddHours(24), schedule.NextRun(now, now));
    }

    [Fact]
    public void A_start_recorded_in_the_future_never_delays_a_job_by_more_than_one_interval()
    {
        var schedule = BackgroundJobSchedule.Every(TimeSpan.FromHours(1));

        // The clock was set back a year after the run.
        Assert.Equal(Noon.AddHours(1), schedule.NextRun(Noon.AddDays(365), now: Noon));
    }

    [Theory]
    [InlineData(15, "2026-11-11T12:07:00Z", "2026-11-11T12:15:00Z")]
    [InlineData(15, "2026-11-11T12:59:59.999999Z", "2026-11-11T13:00:00Z")]
    [InlineData(60, "2026-11-11T12:07:00Z", "2026-11-11T13:00:00Z")]
    [InlineData(240, "2026-11-11T12:07:00Z", "2026-11-11T16:00:00Z")]
    [InlineData(240, "2026-11-11T20:00:01Z", "2026-11-12T00:00:00Z")]
    [InlineData(1440, "2026-11-11T12:07:00Z", "2026-11-12T00:00:00Z")]
    public void A_boundary_schedule_waits_for_the_next_UTC_boundary(int minutes, string now, string expected)
    {
        var schedule = BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(minutes));

        Assert.Equal(
            DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture),
            schedule.NextRun(previousStart: null, DateTimeOffset.Parse(now, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void A_boundary_schedule_does_not_go_by_the_previous_run()
    {
        var schedule = BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(15));

        // The boundaries since the previous run are not made up for: the next one is in the future.
        Assert.Equal(Noon.AddHours(3).AddMinutes(15), schedule.NextRun(Noon, now: Noon.AddHours(3).AddMinutes(7)));
    }

    [Fact]
    public void A_moment_exactly_on_a_boundary_waits_for_the_following_one()
    {
        var schedule = BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromMinutes(15));

        Assert.Equal(Noon.AddMinutes(15), schedule.NextRun(previousStart: null, now: Noon));
    }

    [Fact]
    public void A_boundary_is_the_same_moment_whatever_offset_the_time_is_given_in()
    {
        var schedule = BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromHours(4));
        var inKathmandu = Noon.AddMinutes(7).ToOffset(new TimeSpan(5, 45, 0));

        var next = schedule.NextRun(previousStart: null, inKathmandu);

        Assert.Equal(Noon.AddHours(4), next);
        Assert.Equal(TimeSpan.Zero, next.Offset);
    }

    [Fact]
    public void An_interval_must_be_positive_and_a_boundary_period_must_divide_a_day()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BackgroundJobSchedule.Every(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => BackgroundJobSchedule.Every(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.Zero));

        // Seven hours would put the boundaries at other times each day.
        Assert.Throws<ArgumentOutOfRangeException>(() => BackgroundJobSchedule.AtUtcBoundaries(TimeSpan.FromHours(7)));
    }
}
