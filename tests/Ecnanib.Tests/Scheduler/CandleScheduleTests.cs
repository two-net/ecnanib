using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Scheduler;

/// <summary>One application whose candle-close jobs every test here asks for their next run, which changes nothing.</summary>
public sealed class SchedulerSchedules(PostgresFixture postgres) : IAsyncDisposable
{
    internal SchedulerApp App { get; } = new(postgres);

    public ValueTask DisposeAsync() => App.DisposeAsync();
}

/// <summary>FR-CYC-01: each timeframe's cycles are due when its candle closes, aligned to UTC, plus the delay.</summary>
public sealed class CandleScheduleTests(SchedulerSchedules schedules) : IClassFixture<SchedulerSchedules>
{
    private static readonly DateTimeOffset Day = SchedulerApp.Day;
    private static readonly TimeSpan Delay = SchedulerApp.Delay;

    /// <summary>A run starts a moment after it's due, and the next is due at the following close.</summary>
    [Theory]
    [InlineData(SchedulerApp.FifteenMinutes, "10:00", "10:15")]
    [InlineData(SchedulerApp.FifteenMinutes, "10:45", "11:00")]
    [InlineData(SchedulerApp.OneHour, "10:00", "11:00")]
    [InlineData(SchedulerApp.OneHour, "23:00", "1.00:00")]
    [InlineData(SchedulerApp.FourHours, "00:00", "04:00")]
    [InlineData(SchedulerApp.FourHours, "04:00", "08:00")]
    [InlineData(SchedulerApp.FourHours, "20:00", "1.00:00")]
    [InlineData(SchedulerApp.OneDay, "00:00", "1.00:00")]
    public void The_next_run_is_at_the_next_close_plus_the_delay(string job, string lastClose, string nextClose)
    {
        var lastStart = Day + TimeSpan.Parse(lastClose) + Delay + TimeSpan.FromMilliseconds(40);

        Assert.Equal(Day + TimeSpan.Parse(nextClose) + Delay, NextRun(job, lastStart, lastStart));
    }

    /// <summary>A run that started late, as a catch-up does, still handled its own close, so the next one is due.</summary>
    [Theory]
    [InlineData(SchedulerApp.FifteenMinutes, "10:04", "10:15")]
    [InlineData(SchedulerApp.OneHour, "10:14", "11:00")]
    [InlineData(SchedulerApp.FourHours, "06:30", "08:00")]
    [InlineData(SchedulerApp.OneDay, "02:59", "1.00:00")]
    public void A_late_run_still_counts_as_its_close(string job, string lastStart, string nextClose)
    {
        var start = Day + TimeSpan.Parse(lastStart);

        Assert.Equal(Day + TimeSpan.Parse(nextClose) + Delay, NextRun(job, start, start));
    }

    /// <summary>A start inside the delay belongs to the close before, which hadn't been run yet.</summary>
    [Fact]
    public void A_start_before_the_delay_has_passed_belongs_to_the_close_before()
    {
        var start = Day + TimeSpan.FromHours(10) + TimeSpan.FromSeconds(5);

        Assert.Equal(Day + TimeSpan.FromHours(10) + Delay, NextRun(SchedulerApp.OneHour, start, start));
    }

    /// <summary>
    /// The application was down from just after 06:00 to 09:20. Three closes were missed, and one run is due, in the
    /// past, so the runner runs it at once. That run decides whether to catch up.
    /// </summary>
    [Fact]
    public void Missed_closes_are_one_run_due_in_the_past()
    {
        var lastStart = Day + TimeSpan.FromHours(6) + Delay;
        var now = Day + new TimeSpan(9, 20, 0);

        Assert.Equal(Day + TimeSpan.FromHours(7) + Delay, NextRun(SchedulerApp.OneHour, lastStart, now));
    }

    /// <summary>A new installation waits for the next close rather than running at once.</summary>
    [Theory]
    [InlineData(SchedulerApp.FifteenMinutes, "10:07", "10:15")]
    [InlineData(SchedulerApp.OneHour, "10:07", "11:00")]
    [InlineData(SchedulerApp.FourHours, "10:07", "12:00")]
    [InlineData(SchedulerApp.OneDay, "10:07", "1.00:00")]
    [InlineData(SchedulerApp.OneHour, "11:00:05", "11:00")]
    public void A_job_that_never_ran_waits_for_the_next_close(string job, string now, string nextClose) =>
        Assert.Equal(Day + TimeSpan.Parse(nextClose) + Delay, NextRun(job, null, Day + TimeSpan.Parse(now)));

    [Fact]
    public void A_job_that_never_ran_is_due_now_when_now_is_a_close_plus_the_delay()
    {
        var now = Day + TimeSpan.FromHours(10) + Delay;

        Assert.Equal(now, NextRun(SchedulerApp.OneHour, null, now));
    }

    [Theory]
    [InlineData(SchedulerApp.FifteenMinutes, "cycles-15m")]
    [InlineData(SchedulerApp.OneHour, "cycles-1h")]
    [InlineData(SchedulerApp.FourHours, "cycles-4h")]
    [InlineData(SchedulerApp.OneDay, "cycles-1d")]
    public void Each_timeframe_has_its_own_job(string job, string name) =>
        Assert.Equal(name, schedules.App.Job(job).Name);

    private DateTimeOffset NextRun(string job, DateTimeOffset? lastStart, DateTimeOffset now) =>
        schedules.App.Job(job).Schedule.NextRun(lastStart, now);
}
