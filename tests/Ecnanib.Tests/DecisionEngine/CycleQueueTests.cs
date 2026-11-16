using Ecnanib.Shared;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// When many agents share a candle boundary, their cycles are queued with concurrency limits per
/// provider and per user key, shortest timeframe first (FR-CYC-11). The limits are those of
/// <c>Trading:Cycles</c> in the configuration file. See docs/cycles.md.
/// </summary>
public class CycleQueueTests
{
    // A close of every timeframe, and a close of the shortest alone.
    private static readonly DateTimeOffset Midnight = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Quarter = new(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task Cycles_of_the_shortest_timeframe_start_first()
    {
        // One cycle at a time, so that the order they start in is the order of the queue.
        using var application = new CyclesApplication(Midnight, maxPerProvider: 1);
        var agents = application.Agents!;

        // Given longest first, and read latest first: the queue has an order of its own.
        var day = agents.Give(Guid.NewGuid(), Timeframe.OneDay);
        var fourHours = agents.Give(Guid.NewGuid(), Timeframe.FourHours);
        var hour = agents.Give(Guid.NewGuid(), Timeframe.OneHour);
        var quarter = agents.Give(Guid.NewGuid(), Timeframe.FifteenMinutes);

        await application.RunAsync(Midnight).Within();

        Assert.Equal([quarter, hour, fourHours, day], application.Cycles.StartedFor);
        Assert.Equal(1, application.Cycles.MostAtOnce);
    }

    [Fact]
    public async Task Within_a_timeframe_the_agents_of_the_users_take_turns()
    {
        using var application = new CyclesApplication(Quarter, maxPerProvider: 1);
        var agents = application.Agents!;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        // Ada has three agents, which were all created before the one of Grace.
        var first = agents.Give(ada);
        var second = agents.Give(ada, symbol: "SOLUSDT");
        var third = agents.Give(ada, market: Market.Futures);
        var other = agents.Give(grace);

        await application.RunAsync(Quarter).Within();

        // The first agent of each user comes before the second of any: a user with many agents
        // does not keep the others waiting for all of them.
        Assert.Equal([first, other, second, third], application.Cycles.StartedFor);
    }

    [Fact]
    public async Task No_more_cycles_use_a_provider_at_once_than_the_file_allows()
    {
        using var application = new CyclesApplication(Quarter, maxPerProvider: 2);
        var agents = application.Agents!;

        // Five users, each with one agent whose team uses Claude.
        Guid[] given = [.. Enumerable.Range(0, 5).Select(_ => agents.Give(Guid.NewGuid()))];

        application.Cycles.Holds = true;
        var run = application.RunAsync(Quarter);

        // Two start, and the others wait: each starts when one that runs has ended.
        await application.Cycles.StartedAsync(2);

        for (var ended = 0; ended < given.Length; ended++)
        {
            application.Cycles.Release(application.Cycles.StartedFor[ended]);

            await application.Cycles.StartedAsync(Math.Min(ended + 3, given.Length));
        }

        await run.Within();

        Assert.Equal(given.Order(), application.Cycles.StartedFor.Order());
        Assert.Equal(2, application.Cycles.MostAtOnceWith("claude"));

        // They start in the order of the queue, which is that of their ids here.
        Assert.Equal(given[2..], application.Cycles.StartedFor.Skip(2));
    }

    [Fact]
    public async Task No_more_cycles_use_the_key_of_a_user_at_once_than_the_file_allows()
    {
        using var application = new CyclesApplication(Quarter, maxPerKey: 2);
        var agents = application.Agents!;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        // A user has one key for a provider (FR-KEY-2), which all their agents are called with.
        var first = agents.Give(ada);
        var second = agents.Give(ada, symbol: "SOLUSDT");
        var third = agents.Give(ada, market: Market.Futures);
        var other = agents.Give(grace);

        application.Cycles.Holds = true;
        var run = application.RunAsync(Quarter);

        // Two of Ada's start, and the one of Grace, whose key is another: the provider has
        // room for all of them.
        await application.Cycles.StartedAsync(3);
        Assert.Equal(new[] { first, second, other }.Order(), application.Cycles.StartedFor.Order());

        // The third of Ada's starts when one of hers has ended.
        application.Cycles.Release(first);
        await application.Cycles.StartedAsync(4);
        Assert.Equal(third, application.Cycles.StartedFor[3]);

        application.Cycles.ReleaseAll();
        await run.Within();

        Assert.Equal(2, application.Cycles.MostAtOnceWith(ada, "claude"));
        Assert.Equal(1, application.Cycles.MostAtOnceWith(grace, "claude"));
        Assert.Equal(3, application.Cycles.MostAtOnceWith("claude"));
    }

    [Fact]
    public async Task A_provider_that_holds_two_roles_counts_once()
    {
        // One place with each provider, and one with each key.
        using var application = new CyclesApplication(Quarter, maxPerProvider: 1, maxPerKey: 1);

        // A reduced team (FR-TEAM-5): two providers hold two roles each.
        var agent = application.Agents!.Give(Guid.NewGuid(), providers: ["claude", "claude", "gpt", "gpt"]);

        // A cycle that needed a place for each role would never find two with one provider.
        await application.RunAsync(Quarter).Within();

        var cycle = Assert.Single(application.Cycles.Started);
        Assert.Equal(agent, cycle.Agent.Id);
        Assert.Equal(["claude", "claude", "gpt", "gpt"], cycle.Roles.Select(role => role.Provider.Value));
    }

    [Fact]
    public async Task A_cycle_that_waits_for_a_busy_provider_holds_back_none_that_does_not_use_it()
    {
        using var application = new CyclesApplication(Midnight, maxPerProvider: 1);
        var agents = application.Agents!;

        // Two agents on the shortest timeframe use Claude, and one on the longest uses GPT.
        var first = agents.Give(Guid.NewGuid(), Timeframe.FifteenMinutes, providers: "claude");
        var second = agents.Give(Guid.NewGuid(), Timeframe.FifteenMinutes, providers: "claude");
        var other = agents.Give(Guid.NewGuid(), Timeframe.OneDay, providers: "gpt");

        application.Cycles.Holds = true;
        var run = application.RunAsync(Midnight);

        // The second waits for Claude. The cycle of a day comes after it in the queue, and
        // starts all the same: it needs nothing that the second waits for (NFR-REL-4).
        await application.Cycles.StartedAsync(2);
        Assert.Equal(new[] { first, other }.Order(), application.Cycles.StartedFor.Order());

        application.Cycles.Release(first);
        await application.Cycles.StartedAsync(3);
        Assert.Equal(second, application.Cycles.StartedFor[2]);

        application.Cycles.ReleaseAll();
        await run.Within();

        Assert.Equal(1, application.Cycles.MostAtOnceWith("claude"));
    }

    [Fact]
    public async Task A_place_is_free_again_however_a_cycle_ends()
    {
        using var application = new CyclesApplication(Quarter, maxPerProvider: 1);
        var agents = application.Agents!;
        var ada = Guid.NewGuid();

        var failing = agents.Give(ada);
        var next = agents.Give(Guid.NewGuid());

        application.Cycles.Fails = cycle =>
            cycle.Agent.Id == failing ? new InvalidOperationException("The inside of the cycle failed.") : null;

        await application.RunAsync(Quarter).Within();

        // The cycle that failed gave its place back, and the next one started.
        Assert.Equal([failing, next], application.Cycles.StartedFor);

        // The failure is logged once, as an error, with the user, the agent, and the cycle
        // (NFR-LOG-3), and ends that cycle alone.
        var error = Assert.Single(application.Written(), written => written.Level is "Error" or "Fatal");
        Assert.Equal(CyclesLog.Failed, error.Template);
        Assert.Equal(failing.ToString(), error.Text("AgentId"));
        Assert.Equal(ada.ToString(), error.Text("UserId"));
        Assert.Equal(application.Cycles.Started[0].CycleId.ToString(), error.Text("CycleId"));
        Assert.Contains("The inside of the cycle failed.", error.Exception);

        var summary = application.SummaryOf(Quarter);
        Assert.Equal((1, 1), (summary["Ended"].GetInt32(), summary["Failed"].GetInt32()));
    }

    [Fact]
    public async Task An_agent_that_stopped_running_while_its_cycle_waited_gets_none()
    {
        using var application = new CyclesApplication(Quarter, maxPerProvider: 1);
        var agents = application.Agents!;

        var first = agents.Give(Guid.NewGuid());
        var paused = agents.Give(Guid.NewGuid());

        application.Cycles.Holds = true;
        var run = application.RunAsync(Quarter);
        await application.Cycles.StartedAsync(1);

        // Its user pauses the second agent while its cycle waits for its turn.
        agents.Stop(paused);

        application.Cycles.ReleaseAll();
        await run.Within();

        // Only a running agent gets a cycle (SRS 3.3): it is asked again as the turn comes.
        Assert.Equal([first], application.Cycles.StartedFor);

        var skipped = Assert.Single(application.Written(CyclesLog.Area, CyclesLog.NoLongerRuns));
        Assert.Equal(paused.ToString(), skipped.Text("AgentId"));
        Assert.Equal("Information", skipped.Level);
        Assert.Equal(1, application.SummaryOf(Quarter)["NotRunning"].GetInt32());
    }
}
