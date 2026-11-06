using Ecnanib.Kernel;
using Ecnanib.Modules.DecisionCycle.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Scheduler;

/// <summary>CFG-03: the cycle timings stop startup with a message that names what's wrong.</summary>
public sealed class SchedulerSettingsTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("Agents:CandleCloseDelaySeconds", "-1")]
    [InlineData("Agents:CandleCloseDelaySeconds", "301")]
    [InlineData("Agents:CandleCloseDelaySeconds", "soon")]
    [InlineData("Agents:MaxConcurrentCycles", "0")]
    [InlineData("Agents:MaxConcurrentCycles", "1001")]
    [InlineData("Agents:MaxCycleJitterSeconds", "21")]
    [InlineData("Agents:CatchUpGraceMinutes:15m", "15")]
    [InlineData("Agents:CatchUpGraceMinutes:1D", "-1")]
    [InlineData("Agents:CatchUpGraceMinutes:30m", "5")]
    [InlineData("Agents:CycleDeadlineMinutes:1H", "0")]
    [InlineData("Agents:CycleDeadlineMinutes:1H", "60")]
    [InlineData("Agents:CycleDeadlineMinutes:4H", "1.5")]
    public async Task An_invalid_setting_stops_startup_and_is_named(string key, string value)
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?> { [key] = value });

        var exception = Assert.ThrowsAny<Exception>(app.Start);

        Assert.Contains(key, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>SRS 7.3's example, which is also every default.</summary>
    [Fact]
    public async Task The_settings_in_the_srs_start()
    {
        await using var app = new AppFactory(postgres.NewConnectionString(), settings: new Dictionary<string, string?>
        {
            ["Agents:CandleCloseDelaySeconds"] = "10",
            ["Agents:MaxConcurrentCycles"] = "50",
            ["Agents:CatchUpGraceMinutes:15m"] = "5",
            ["Agents:CatchUpGraceMinutes:1H"] = "15",
            ["Agents:CatchUpGraceMinutes:4H"] = "60",
            ["Agents:CatchUpGraceMinutes:1D"] = "180",
            ["Agents:CycleDeadlineMinutes:15m"] = "3",
            ["Agents:CycleDeadlineMinutes:1H"] = "10",
            ["Agents:CycleDeadlineMinutes:4H"] = "20",
            ["Agents:CycleDeadlineMinutes:1D"] = "30",
        });

        app.Start();
    }

    /// <summary>NFR-PERF-02's deadlines and the delay apply when nothing is configured.</summary>
    [Theory]
    [InlineData(SchedulerApp.FifteenMinutes, Timeframe.FifteenMinutes, 3)]
    [InlineData(SchedulerApp.OneHour, Timeframe.OneHour, 10)]
    [InlineData(SchedulerApp.FourHours, Timeframe.FourHours, 20)]
    [InlineData(SchedulerApp.OneDay, Timeframe.OneDay, 30)]
    public async Task The_default_deadlines_are_nfr_perf_02s(string job, Timeframe timeframe, int minutes)
    {
        await using var app = new SchedulerApp(postgres);
        app.Agents.Add(timeframe);

        // Midnight is a close for every timeframe.
        await app.RunAtAsync(job, SchedulerApp.Day + SchedulerApp.Delay);

        await app.Cycles.UntilStartedAsync(1);
        Assert.Equal(SchedulerApp.Day + SchedulerApp.Delay + TimeSpan.FromMinutes(minutes), Assert.Single(app.Cycles.Started).Deadline);
    }

    [Fact]
    public async Task The_delay_moves_every_run()
    {
        await using var app = new SchedulerApp(postgres, new() { ["Agents:CandleCloseDelaySeconds"] = "45" });
        var lastStart = SchedulerApp.Day + TimeSpan.FromSeconds(45);

        Assert.Equal(
            SchedulerApp.Day + TimeSpan.FromHours(1) + TimeSpan.FromSeconds(45),
            app.Job(SchedulerApp.OneHour).Schedule.NextRun(lastStart, lastStart));
    }

    /// <summary>FR-CYC-06: the check the cycle makes right before an entry.</summary>
    [Fact]
    public void A_new_entry_is_allowed_only_before_the_deadline()
    {
        var deadline = SchedulerApp.Day + TimeSpan.FromMinutes(3);
        var request = new CycleRequest(Guid.NewGuid(), Guid.NewGuid(), Timeframe.FifteenMinutes, SchedulerApp.Day, deadline, CatchUp: false);

        Assert.True(request.AllowsNewEntry(deadline - TimeSpan.FromTicks(1)));
        Assert.False(request.AllowsNewEntry(deadline));
        Assert.False(request.AllowsNewEntry(deadline + TimeSpan.FromMinutes(1)));
    }
}
