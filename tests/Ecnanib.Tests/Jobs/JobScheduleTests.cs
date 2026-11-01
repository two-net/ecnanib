using Ecnanib.Platform.Contracts;

namespace Ecnanib.Tests.Jobs;

public sealed class JobScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_job_that_has_never_run_is_due_now() =>
        Assert.Equal(Now, JobSchedule.Every(TimeSpan.FromHours(1)).NextRun(null, Now));

    [Fact]
    public void The_next_run_is_one_interval_after_the_last_start() =>
        Assert.Equal(
            Now.AddMinutes(20),
            JobSchedule.Every(TimeSpan.FromHours(1)).NextRun(Now.AddMinutes(-40), Now));

    /// <summary>The runner runs anything in the past once, so ten missed ticks are one run.</summary>
    [Fact]
    public void A_late_job_is_due_in_the_past_rather_than_once_per_missed_interval() =>
        Assert.Equal(
            Now.AddHours(-9),
            JobSchedule.Every(TimeSpan.FromHours(1)).NextRun(Now.AddHours(-10), Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_interval_must_be_positive(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => JobSchedule.Every(TimeSpan.FromSeconds(seconds)));
}
