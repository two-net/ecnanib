using Ecnanib.Kernel;
using Ecnanib.Platform.Contracts;
using Ecnanib.Tests.Persistence;

namespace Ecnanib.Tests.Scheduler;

/// <summary>
/// What a candle-close job does with the agents it finds: on time, as a catch-up or not at all (FR-CYC-01, FR-CYC-04),
/// with a deadline (FR-CYC-06), through a fair, limited queue (FR-CYC-08), each cycle a run of its own (NFR-REL-05).
/// </summary>
public sealed class CycleSchedulingTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset TenAm = SchedulerApp.Day + TimeSpan.FromHours(10);
    private static readonly DateTimeOffset Due = TenAm + SchedulerApp.Delay;

    /// <summary>A few seconds after the close and the delay, as the runner's loop would wake.</summary>
    private static readonly DateTimeOffset OnTime = Due + TimeSpan.FromSeconds(2);

    [Fact]
    public async Task An_on_time_close_runs_each_active_agent_of_its_timeframe_once()
    {
        await using var app = new SchedulerApp(postgres);
        var hourly = app.Agents.Add(Timeframe.OneHour, count: 2);
        app.Agents.Add(Timeframe.FifteenMinutes);
        app.Agents.Add(Timeframe.FourHours);

        var context = await app.RunAtAsync(SchedulerApp.OneHour, OnTime);

        await app.Cycles.UntilStartedAsync(2);
        Assert.Equal(hourly.Select(agent => agent.Id).Order(), app.Cycles.Started.Select(cycle => cycle.AgentId).Order());

        Assert.All(app.Cycles.Started, cycle =>
        {
            Assert.Equal(Timeframe.OneHour, cycle.Timeframe);
            Assert.Equal(TenAm, cycle.CandleClose);
            Assert.Equal(Due + TimeSpan.FromMinutes(10), cycle.Deadline);
            Assert.False(cycle.CatchUp);
            Assert.Equal(hourly.Single(agent => agent.Id == cycle.AgentId).UserId, cycle.UserId);
        });

        Assert.Contains("started 2,", context.Details, StringComparison.Ordinal);
    }

    /// <summary>A 4H close at 08:00 is also a 1H and a 15m close, and each job runs only its own agents.</summary>
    [Fact]
    public async Task Each_timeframe_runs_at_its_own_closes()
    {
        await using var app = new SchedulerApp(postgres);
        var fourHourly = app.Agents.Add(Timeframe.FourHours).Single();
        var daily = app.Agents.Add(Timeframe.OneDay).Single();

        await app.RunAtAsync(SchedulerApp.FourHours, SchedulerApp.Day + TimeSpan.FromHours(8) + SchedulerApp.Delay);
        await app.RunAtAsync(SchedulerApp.OneDay, SchedulerApp.Day + TimeSpan.FromDays(1) + SchedulerApp.Delay);

        await app.Cycles.UntilStartedAsync(2);
        Assert.Collection(
            app.Cycles.Started,
            cycle => Assert.Equal((fourHourly.Id, SchedulerApp.Day + TimeSpan.FromHours(8)), (cycle.AgentId, cycle.CandleClose)),
            cycle => Assert.Equal((daily.Id, SchedulerApp.Day + TimeSpan.FromDays(1)), (cycle.AgentId, cycle.CandleClose)));
    }

    [Fact]
    public async Task Each_cycle_is_a_run_of_its_own()
    {
        await using var app = new SchedulerApp(postgres);
        var agents = app.Agents.Add(Timeframe.OneHour, count: 3);

        await app.RunAtAsync(SchedulerApp.OneHour, OnTime);

        var runs = await app.FinishedCyclesAsync(3);
        Assert.Equal(3, runs.Count);
        Assert.All(agents, agent => Assert.Single(runs, run => run.Contains(agent.Id.ToString(), StringComparison.Ordinal)));
        Assert.All(runs, run => Assert.StartsWith("Succeeded", run, StringComparison.Ordinal));

        // Each run's last word is its correlation ID, and no two share one (NFR-OBS-02).
        Assert.Equal(3, runs.Select(run => run.Split(' ')[^1]).Distinct().Count());
    }

    /// <summary>FR-CYC-04: back 3 minutes after an hourly close, which is within the 15-minute grace.</summary>
    [Fact]
    public async Task A_late_close_within_its_grace_runs_one_catch_up_with_a_deadline_from_now()
    {
        await using var app = new SchedulerApp(postgres);
        var agent = app.Agents.Add(Timeframe.OneHour).Single();
        var now = Due + TimeSpan.FromMinutes(3);

        var context = await app.RunAtAsync(SchedulerApp.OneHour, now);

        await app.Cycles.UntilStartedAsync(1);
        var cycle = Assert.Single(app.Cycles.Started);
        Assert.Equal(agent.Id, cycle.AgentId);
        Assert.Equal(TenAm, cycle.CandleClose);
        Assert.True(cycle.CatchUp);
        Assert.InRange(cycle.Deadline, now + TimeSpan.FromMinutes(10), now + TimeSpan.FromMinutes(11));
        Assert.Contains("catch-up", context.Details, StringComparison.Ordinal);
    }

    /// <summary>FR-CYC-04: back 20 minutes after an hourly close, which is past the 15-minute grace.</summary>
    [Fact]
    public async Task A_close_past_its_grace_is_skipped_and_logged_for_each_agent()
    {
        await using var app = new SchedulerApp(postgres);
        var agents = app.Agents.Add(Timeframe.OneHour, count: 2);

        var context = await app.RunAtAsync(SchedulerApp.OneHour, Due + TimeSpan.FromMinutes(20));

        Assert.Equal($"Candle {TenAm:O}, 2 skipped as too late", context.Details);
        await app.UntilLoggedAsync([.. agents.Select(agent => agent.Id.ToString()), "past the catch-up grace"]);
        Assert.Empty(app.Cycles.Started);
    }

    /// <summary>Only the latest close is considered, so however long it was down, an agent catches up once at most.</summary>
    [Fact]
    public async Task After_several_missed_closes_only_the_latest_is_caught_up()
    {
        await using var app = new SchedulerApp(postgres);
        app.Agents.Add(Timeframe.FifteenMinutes);

        await app.RunAtAsync(SchedulerApp.FifteenMinutes, TenAm + new TimeSpan(1, 2, 0));

        await app.Cycles.UntilStartedAsync(1);
        var cycle = Assert.Single(app.Cycles.Started);
        Assert.Equal(TenAm + TimeSpan.FromHours(1), cycle.CandleClose);
        Assert.True(cycle.CatchUp);
    }

    [Fact]
    public async Task A_grace_of_zero_turns_catch_up_off_but_not_on_time_cycles()
    {
        await using var app = new SchedulerApp(postgres, new() { ["Agents:CatchUpGraceMinutes:1H"] = "0" });
        app.Agents.Add(Timeframe.OneHour);

        var late = await app.RunAtAsync(SchedulerApp.OneHour, Due + TimeSpan.FromMinutes(1));
        Assert.Contains("skipped as too late", late.Details, StringComparison.Ordinal);

        await app.RunAtAsync(SchedulerApp.OneHour, OnTime + TimeSpan.FromHours(1));
        await app.Cycles.UntilStartedAsync(1);
        Assert.False(Assert.Single(app.Cycles.Started).CatchUp);
    }

    /// <summary>FR-CYC-08: at most two at once, and the rest start as places free up.</summary>
    [Fact]
    public async Task The_limit_holds_cycles_back_until_one_ends()
    {
        await using var app = new SchedulerApp(postgres, new() { ["Agents:MaxConcurrentCycles"] = "2" });
        foreach (var agent in app.Agents.Add(Timeframe.OneHour, count: 5))
        {
            app.Cycles.Hold(agent.Id);
        }

        var running = app.RunAtAsync(SchedulerApp.OneHour, OnTime);

        await app.Cycles.UntilStartedAsync(2);
        await Task.Delay(TimeSpan.FromMilliseconds(300), SchedulerApp.Cancellation);
        Assert.Equal(2, app.Cycles.Started.Count);
        Assert.False(running.IsCompleted);

        app.Cycles.Hold(app.Cycles.Started[0].AgentId).Open();
        await app.Cycles.UntilStartedAsync(3);

        app.Cycles.OpenAll();
        var context = await running;
        Assert.Contains("started 5,", context.Details, StringComparison.Ordinal);
        await app.Cycles.UntilStartedAsync(5);
    }

    /// <summary>
    /// FR-CYC-08: one place, and one user with four agents queued before another's one. The other user's goes next,
    /// whatever the order they arrived in.
    /// </summary>
    [Fact]
    public async Task Users_take_turns_in_the_queue()
    {
        await using var app = new SchedulerApp(postgres, new() { ["Agents:MaxConcurrentCycles"] = "1" });
        var busy = Guid.NewGuid();
        var many = app.Agents.Add(Timeframe.OneHour, count: 4, userId: busy);
        var one = app.Agents.Add(Timeframe.OneHour).Single();

        foreach (var agent in many.Append(one))
        {
            app.Cycles.Hold(agent.Id);
        }

        var running = app.RunAtAsync(SchedulerApp.OneHour, OnTime);

        for (var started = 1; started <= 5; started++)
        {
            await app.Cycles.UntilStartedAsync(started);
            app.Cycles.Hold(app.Cycles.Started[^1].AgentId).Open();
        }

        await running;
        var order = app.Cycles.Started.Select(cycle => cycle.UserId).ToList();
        Assert.InRange(order.IndexOf(one.UserId), 0, 1);
    }

    /// <summary>FR-CYC-06: a cycle still queued when its deadline passes could enter nothing, so it's dropped.</summary>
    [Fact]
    public async Task A_cycle_still_queued_at_its_deadline_is_dropped_and_logged()
    {
        await using var app = new SchedulerApp(postgres, new() { ["Agents:MaxConcurrentCycles"] = "1" });
        var agents = app.Agents.Add(Timeframe.OneHour, count: 2);
        app.Cycles.Hold(agents[0].Id);
        app.Cycles.Hold(agents[1].Id);

        var running = app.RunAtAsync(SchedulerApp.OneHour, OnTime);
        await app.Cycles.UntilStartedAsync(1);

        app.SetClock(Due + TimeSpan.FromMinutes(11));
        app.Cycles.OpenAll();
        var context = await running;

        Assert.Single(app.Cycles.Started);
        Assert.Contains("started 1, still running 0, past deadline 1,", context.Details, StringComparison.Ordinal);
        var dropped = agents.Single(agent => agent.Id != app.Cycles.Started[0].AgentId);
        await app.UntilLoggedAsync(dropped.Id.ToString(), "still queued at its deadline");
    }

    /// <summary>FR-CYC-02: an agent never has two cycles at once. The next close finds the last still going, and skips it.</summary>
    [Fact]
    public async Task An_agent_whose_previous_cycle_is_still_running_is_skipped()
    {
        await using var app = new SchedulerApp(postgres);
        var agent = app.Agents.Add(Timeframe.FifteenMinutes).Single();
        var gate = app.Cycles.Hold(agent.Id);

        // The job returns once its cycles have started, so a cycle that runs long can't hold up the next close.
        await app.RunAtAsync(SchedulerApp.FifteenMinutes, OnTime);
        await gate.UntilReachedAsync();

        var next = await app.RunAtAsync(SchedulerApp.FifteenMinutes, OnTime + TimeSpan.FromMinutes(15));

        Assert.Contains("started 0, still running 1,", next.Details, StringComparison.Ordinal);
        Assert.Single(app.Cycles.Started);
        await app.UntilLoggedAsync(agent.Id.ToString(), "previous cycle is still running");

        gate.Open();
        await app.FinishedCyclesAsync(1);
        await app.RunAtAsync(SchedulerApp.FifteenMinutes, OnTime + TimeSpan.FromMinutes(30));
        await app.Cycles.UntilStartedAsync(2);
    }

    /// <summary>NFR-REL-05: one agent's cycle failing affects no other agent's.</summary>
    [Fact]
    public async Task One_cycle_failing_leaves_the_others_alone()
    {
        await using var app = new SchedulerApp(postgres);
        var agents = app.Agents.Add(Timeframe.OneHour, count: 3);
        app.Cycles.Fail(agents[0].Id);

        await app.RunAtAsync(SchedulerApp.OneHour, OnTime);

        // A failed run stores the exception's type, never its text, so the agent is in its log rather than its row.
        var runs = await app.FinishedCyclesAsync(3);
        Assert.Single(runs, run => run.StartsWith("Failed System.InvalidOperationException", StringComparison.Ordinal));
        Assert.All(agents[1..], agent => Assert.Single(
            runs,
            run => run.StartsWith("Succeeded", StringComparison.Ordinal) && run.Contains(agent.Id.ToString(), StringComparison.Ordinal)));
        await app.UntilLoggedAsync(agents[0].Id.ToString(), "A test cycle failed");
    }

    /// <summary>NFR-REL-04: what hasn't started when shutdown begins never does, and the run says it was interrupted.</summary>
    [Fact]
    public async Task Shutdown_starts_nothing_more()
    {
        await using var app = new SchedulerApp(postgres, new() { ["Agents:MaxConcurrentCycles"] = "1" });
        var agents = app.Agents.Add(Timeframe.OneHour, count: 2);
        app.Cycles.Hold(agents[0].Id);
        app.Cycles.Hold(agents[1].Id);
        using var stopping = new CancellationTokenSource();

        var running = app.RunAtAsync(SchedulerApp.OneHour, OnTime, new JobContext(stopping.Token));
        await app.Cycles.UntilStartedAsync(1);
        await stopping.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        app.Cycles.OpenAll();
        await app.FinishedCyclesAsync(1);
        Assert.Single(app.Cycles.Started);
    }
}
