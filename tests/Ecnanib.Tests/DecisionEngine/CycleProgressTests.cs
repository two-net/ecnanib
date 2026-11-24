using Ecnanib.Agents;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.LiveUpdates;
using Ecnanib.Shared;
using Ecnanib.Tests.Agents;
using Ecnanib.Tests.LiveUpdates;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// Cycle progress updates in the UI without a manual refresh (FR-NTF-2). A cycle is recorded
/// once, when it ends, so while it runs it says each step that it comes to, in memory, for its
/// user alone, and tells the open pages of that user that it moved on. See "The progress of a
/// cycle" in docs/cycles.md.
/// </summary>
/// <remarks>
/// The models, the keys, the records, and what places the orders are stand-ins, and the
/// exchange has all the market data. A test holds the call of a role to look at a cycle while
/// it runs.
/// </remarks>
public class CycleProgressTests
{
    private const string Ada = "ada@example.com";
    private const string Grace = "grace@example.com";

    /// <summary>A close of a candle of 15 minutes, which is when the cycles of these tests run.</summary>
    private static readonly DateTimeOffset Quarter = new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_cycle_says_each_step_that_it_comes_to_for_its_user_alone_and_runs_no_longer_once_it_is_recorded()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var progress = cycles.Application.CycleProgress;
        var (user, another) = (Guid.NewGuid(), Guid.NewGuid());
        var agent = cycles.Agents!.Give(user);
        var theirs = cycles.Agents!.Give(another);

        await cycles.Application.LiveSignals.ListensAsync();
        using var follows = new Following(cycles.Application.LiveSignals, user);

        Assert.Empty(progress.OfAgent(user, agent));

        // No call ends until the test lets it.
        cycles.Models.Hold(Enum.GetValues<Role>());
        var run = cycles.RunAsync(Quarter);

        // Step 3: the two analysts are being called. Before them, the orders of the agent were
        // settled, its loss limits were evaluated, and its data package was built.
        await cycles.Models.StartedAsync(Role.MarketAnalyst, calls: 2);
        await cycles.Models.StartedAsync(Role.SentimentAnalyst, calls: 2);

        var running = Assert.Single(progress.OfAgent(user, agent));

        Assert.Equal((CycleKind.Scheduled, Quarter, Quarter), (running.Kind, running.ClosedAt, running.StartedAt));
        Assert.Equal([CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts], running.Steps.Select(reached => reached.Step));
        Assert.All(running.Steps, reached => Assert.Equal(Quarter, reached.At));

        // It is asked for by owner: the cycle of an agent of another user is not found, and
        // each user finds the cycle of their own agent.
        Assert.Empty(progress.OfAgent(another, agent));
        Assert.Empty(progress.OfAgent(user, theirs));
        Assert.Empty(progress.OfAgent(user, Guid.NewGuid()));
        Assert.NotEqual(running.CycleId, Assert.Single(progress.OfAgent(another, theirs)).CycleId);

        // Step 4, and step 5 for a plan that opens a position.
        cycles.Models.Release(Role.MarketAnalyst);
        cycles.Models.Release(Role.SentimentAnalyst);
        await cycles.Models.StartedAsync(Role.Strategist, calls: 2);

        Assert.Equal(CycleStep.Strategist, Assert.Single(progress.OfAgent(user, agent)).Steps[^1].Step);

        cycles.Models.Release(Role.Strategist);
        await cycles.Models.StartedAsync(Role.RiskManager, calls: 2);

        Assert.Equal(
            [CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts, CycleStep.Strategist, CycleStep.RiskManager],
            Assert.Single(progress.OfAgent(user, agent)).Steps.Select(reached => reached.Step));

        cycles.Models.Release(Role.RiskManager);
        await run.Within();

        // The cycle is recorded, and runs no longer: its record says the rest.
        Assert.Empty(progress.OfAgent(user, agent));
        Assert.Empty(progress.OfAgent(another, theirs));
        Assert.Equal(CycleOutcome.Approved, cycles.Records.Of(agent).Cycle.Outcome);

        // The open pages of its user were told each time: as it started, at each of its eight
        // steps, and as it ended. Nothing of what it decided is in what they were told.
        Assert.Equal(Enumerable.Repeat(new LiveSignal(LiveSubject.CycleStep, agent), 10), follows.Heard);
    }

    [Fact]
    public async Task A_cycle_that_goes_all_the_way_comes_to_eight_steps_in_the_order_of_the_decision_cycle()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var user = Guid.NewGuid();
        var agent = cycles.Agents!.Give(user);

        await cycles.Application.LiveSignals.ListensAsync();
        using var watching = Watching(cycles, user, agent);

        await cycles.RunAsync(Quarter).Within();

        // What a page that asked at every signal saw last, before the cycle was gone.
        var last = watching.Last;

        Assert.NotNull(last);
        Assert.Equal(
            [
                CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts, CycleStep.Strategist,
                CycleStep.RiskManager, CycleStep.RiskGuard, CycleStep.Orders, CycleStep.Recording,
            ],
            last.Steps.Select(reached => reached.Step));

        // It placed what the Risk Guard accepted, and is recorded under the id that it ran with.
        Assert.Equal(last.CycleId, Assert.Single(cycles.Orders.Placed).CycleId);
        Assert.Equal(last.CycleId, cycles.Records.Of(agent).Cycle.Id);
        Assert.Empty(cycles.Application.CycleProgress.OfAgent(user, agent));
    }

    [Fact]
    public async Task A_cycle_comes_only_to_the_steps_that_its_decision_leads_to()
    {
        using var cycles = new CyclesApplication(Quarter, buildsPackages: true);
        var user = Guid.NewGuid();
        var agent = cycles.Agents!.Give(user);

        // The Strategist gives no valid result, twice: the cycle ends with no decision.
        cycles.Models.Says(Role.Strategist, "This is no plan.", "Neither is this.");

        await cycles.Application.LiveSignals.ListensAsync();
        using var watching = Watching(cycles, user, agent);

        await cycles.RunAsync(Quarter).Within();

        Assert.Equal(CycleOutcome.NoDecision, cycles.Records.Of(agent).Cycle.Outcome);

        // No plan was reviewed, checked, or placed, and the cycle is recorded all the same.
        Assert.Equal(
            [CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts, CycleStep.Strategist, CycleStep.Recording],
            watching.Last!.Steps.Select(reached => reached.Step));
    }

    [Fact]
    public async Task A_test_cycle_is_told_apart_settles_and_places_nothing_and_runs_beside_the_cycle_of_a_close()
    {
        using var cycles = new CyclesApplication(Quarter, storedAgents: true, buildsPackages: true);
        var application = cycles.Application;
        var progress = application.CycleProgress;
        var ada = await application.RegisterVerifiedAsync(Ada);
        var grace = await application.RegisterVerifiedAsync(Grace);
        var agent = await application.GiveAgentAsync(
            ada.Id, Quarter.AddDays(-1), state: AgentState.Running, timeframe: Timeframe.FifteenMinutes);

        await application.LiveSignals.ListensAsync();
        using var graceFollows = new Following(application.LiveSignals, grace.Id);

        // The cycle of the close runs, and its user runs a test cycle of the same agent beside it.
        cycles.Models.Hold(Role.MarketAnalyst);

        var ofTheClose = cycles.RunAsync(Quarter);
        await cycles.Models.StartedAsync(Role.MarketAnalyst);

        var test = application.TestCycles.RunAsync(ada.Id, agent, TestContext.Current.CancellationToken);
        await cycles.Models.StartedAsync(Role.MarketAnalyst, calls: 2);

        // Each is kept under its own id, and says its own steps: a test cycle settles nothing.
        var both = progress.OfAgent(ada.Id, agent);

        Assert.Equal(2, both.Count);
        Assert.Equal(2, both.Select(running => running.CycleId).Distinct().Count());
        Assert.Equal(
            [CycleStep.Settling, CycleStep.DataPackage, CycleStep.Analysts],
            both.Single(running => running.Kind == CycleKind.Scheduled).Steps.Select(reached => reached.Step));
        Assert.Equal(
            [CycleStep.DataPackage, CycleStep.Analysts],
            both.Single(running => running.Kind == CycleKind.Test).Steps.Select(reached => reached.Step));
        Assert.All(both, running => Assert.Equal(Quarter, running.ClosedAt));

        // Another user finds neither.
        Assert.Empty(progress.OfAgent(grace.Id, agent));

        // A page that asks at every signal sees the test cycle through to its record: it comes
        // to no orders, for it places none.
        using var watching = Watching(cycles, ada.Id, agent, CycleKind.Test);

        cycles.Models.Release(Role.MarketAnalyst);

        Assert.Equal(TestCycleOutcome.Recorded, (await test.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)).Outcome);
        await ofTheClose.Within();

        Assert.Equal(
            [CycleStep.DataPackage, CycleStep.Analysts, CycleStep.Strategist, CycleStep.RiskManager, CycleStep.RiskGuard, CycleStep.Recording],
            watching.Last!.Steps.Select(reached => reached.Step));

        Assert.Empty(progress.OfAgent(ada.Id, agent));

        // Nothing of it was told to anyone but its user.
        Assert.Empty(graceFollows.Heard);
    }

    /// <summary>Asks for the cycle of an agent at every signal of its user, as a page does, and keeps what it saw last.</summary>
    private static Watched Watching(CyclesApplication cycles, Guid userId, Guid agentId, CycleKind kind = CycleKind.Scheduled) =>
        new(cycles.Application.LiveSignals, cycles.Application.CycleProgress, userId, agentId, kind);

    private sealed class Watched : IDisposable
    {
        private readonly IDisposable _following;
        private RunningCycle? _last;

        public Watched(ILiveSignals signals, ICycleProgress progress, Guid userId, Guid agentId, CycleKind kind)
        {
            _following = signals.Follow(
                userId,
                signals.Mark(),
                signal =>
                {
                    if (signal == new LiveSignal(LiveSubject.CycleStep, agentId)
                        && progress.OfAgent(userId, agentId).SingleOrDefault(running => running.Kind == kind) is { } running)
                    {
                        Volatile.Write(ref _last, running);
                    }
                });
        }

        public RunningCycle? Last => Volatile.Read(ref _last);

        public void Dispose() => _following.Dispose();
    }
}
