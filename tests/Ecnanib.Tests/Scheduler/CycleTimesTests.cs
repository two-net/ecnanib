using Ecnanib.Kernel;
using Ecnanib.Modules.Scheduler.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Scheduler;

/// <summary>
/// UC-01 shows when a new agent's first cycle runs: the next candle close after now, aligned to UTC, plus the delay
/// (FR-CYC-01), as the candle-close jobs run them.
/// </summary>
public sealed class CycleTimesTests(SchedulerSchedules schedules) : IClassFixture<SchedulerSchedules>
{
    private static readonly DateTimeOffset Day = SchedulerApp.Day;
    private static readonly TimeSpan Delay = SchedulerApp.Delay;

    [Theory]
    [InlineData(Timeframe.FifteenMinutes, "10:07", "10:15")]
    [InlineData(Timeframe.OneHour, "10:20", "11:00")]
    [InlineData(Timeframe.FourHours, "05:59", "08:00")]
    [InlineData(Timeframe.OneDay, "13:00", "1.00:00")]
    public void The_next_cycle_is_at_the_next_close_plus_the_delay(Timeframe timeframe, string now, string close)
    {
        Assert.Equal(Day + TimeSpan.Parse(close) + Delay, NextCycleAt(timeframe, Day + TimeSpan.Parse(now)));
    }

    /// <summary>Within the delay after a close, that close's cycle hasn't run yet, so it's the next.</summary>
    [Fact]
    public void Just_after_a_close_its_cycle_is_still_to_come()
    {
        var close = Day + TimeSpan.FromHours(10);

        Assert.Equal(close + Delay, NextCycleAt(Timeframe.OneHour, close));
    }

    /// <summary>A cycle due at this moment may already have listed its agents, so the one after is shown.</summary>
    [Fact]
    public void A_cycle_due_now_is_left_out()
    {
        var due = Day + TimeSpan.FromHours(10) + Delay;

        Assert.Equal(due + TimeSpan.FromHours(1), NextCycleAt(Timeframe.OneHour, due));
    }

    private DateTimeOffset NextCycleAt(Timeframe timeframe, DateTimeOffset now)
    {
        schedules.App.SetClock(now);

        return schedules.App.App.Services.GetRequiredService<ICycleTimes>().NextCycleAt(timeframe);
    }
}
