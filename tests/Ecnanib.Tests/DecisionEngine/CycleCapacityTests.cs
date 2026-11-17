using System.Diagnostics;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// With providers responding normally, 95% of cycles finish within 90 seconds of candle close
/// (NFR-PRF-2). What a cycle does once it has started is the calls of its team, which take as
/// long as the providers do. What is held to here is the share of what starts a cycle: the
/// queue takes no time worth counting for the agents of a whole service, and every close says
/// how many of its cycles ended in time. See "How long a cycle takes" in docs/cycles.md.
/// </summary>
public class CycleCapacityTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Quarter = new(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task Five_hundred_cycles_that_share_a_boundary_pass_the_queue_in_seconds()
    {
        // The limits of the tracked file, and the agents that a single instance is to carry
        // (NFR-PRF-3): 100 users with 5 agents each, on every timeframe, each with a team of
        // four providers, at midnight, when a candle of every timeframe closes.
        using var application = new CyclesApplication(Midnight, maxPerProvider: 250, maxPerKey: 4);
        var agents = application.Agents!;

        (Timeframe Timeframe, string Symbol, Market Market, TradingMode Mode)[] ofUser =
        [
            (Timeframe.FifteenMinutes, "BTCUSDT", Market.Spot, TradingMode.Demo),
            (Timeframe.FifteenMinutes, "SOLUSDT", Market.Spot, TradingMode.Demo),
            (Timeframe.OneHour, "BTCUSDT", Market.Futures, TradingMode.Demo),
            (Timeframe.FourHours, "BTCUSDT", Market.Spot, TradingMode.Live),
            (Timeframe.OneDay, "SOLUSDT", Market.Spot, TradingMode.Live),
        ];

        var users = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToList();
        var given = users
            .SelectMany(user => ofUser.Select(agent =>
                agents.Give(user, agent.Timeframe, agent.Symbol, agent.Market, agent.Mode, "gpt", "grok", "gemini", "claude")))
            .ToList();

        var taken = Stopwatch.StartNew();
        await application.RunAsync(Midnight).Within();
        taken.Stop();

        // Every agent had its cycle, once, within the limits.
        Assert.Equal(given.Order(), application.Cycles.StartedFor.Order());
        Assert.All(new[] { "gpt", "grok", "gemini", "claude" }, provider =>
            Assert.InRange(application.Cycles.MostAtOnceWith(provider), 1, 250));
        Assert.All(users, user => Assert.InRange(application.Cycles.MostAtOnceWith(user, "claude"), 1, 4));

        // The candles were asked for once for each symbol and timeframe, and not once for each agent.
        Assert.Equal(ofUser.Length, application.Candles.Asked.Count);

        // What starts the cycles takes seconds for all of them, of the 30 that the cycles of a
        // shared boundary have to start in, and of the 90 that a cycle has to end in.
        Assert.True(taken.Elapsed < TimeSpan.FromSeconds(20), $"The cycles of 500 agents took {taken.Elapsed} to pass the queue.");

        var summary = application.SummaryOf(Midnight);
        Assert.Equal(("Information", 500, 0), (summary.Level, summary["Ended"].GetInt32(), summary["EndedLate"].GetInt32()));
    }

    [Theory]
    [InlineData(20, 1, "Information")]
    [InlineData(21, 2, "Warning")]
    public async Task The_close_says_how_many_of_its_cycles_ended_within_ninety_seconds_and_warns_when_fewer_than_95_of_100_did(
        int cycles, int late, string level)
    {
        using var application = new CyclesApplication(Quarter);
        var agents = application.Agents!;

        Guid[] given = [.. Enumerable.Range(0, cycles).Select(_ => agents.Give(Guid.NewGuid()))];

        application.Cycles.Holds = true;
        var run = application.RunAsync(Quarter);
        await application.Cycles.StartedAsync(cycles);

        // Most end at once.
        foreach (var agent in given[late..])
        {
            application.Cycles.Release(agent);
        }

        await Wait.UntilAsync(
            () => application.Written(CyclesLog.Area, CyclesLog.Ended).Count == cycles - late, "the cycles that end in time");

        // The others end a second later than 90 seconds after the close.
        application.Clock.Set(Quarter.AddSeconds(91));
        application.Cycles.ReleaseAll();
        await run.Within();

        var summary = application.SummaryOf(Quarter);
        Assert.Equal((cycles - late, late), (summary["Ended"].GetInt32(), summary["EndedLate"].GetInt32()));
        Assert.Equal(90, summary["TargetSeconds"].GetInt32());

        // One in twenty may be late (NFR-PRF-2). More than that is a warning, once for the close.
        Assert.Equal(level, summary.Level);
    }
}
