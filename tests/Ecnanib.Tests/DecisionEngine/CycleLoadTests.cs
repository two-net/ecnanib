using Ecnanib.DecisionEngine;
using Ecnanib.Tests.Logging;

namespace Ecnanib.Tests.DecisionEngine;

/// <summary>
/// The depth of the queue of the cycles, as an operator status screen shows it (FR-OPS-2): the
/// cycles of the close that is under way, by how far each has come. The queue of a close is in
/// memory, and nothing of it is stored (FR-CYC-8, FR-CYC-11). See "The depth of the queue" in
/// docs/cycles.md.
/// </summary>
public class CycleLoadTests
{
    private static readonly DateTimeOffset Quarter = new(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public void Between_two_closes_no_close_is_under_way()
    {
        using var application = new CyclesApplication(Quarter.AddMinutes(-8));
        application.Agents!.Give(Guid.NewGuid());

        Assert.Null(Load(application).Now());
    }

    [Fact]
    public async Task While_the_cycles_of_a_close_run_the_load_says_how_many_wait_and_how_many_run()
    {
        // One cycle at a time uses the one provider of these agents.
        using var application = new CyclesApplication(Quarter, maxPerProvider: 1);
        var agents = application.Agents!;
        var first = agents.Give(Guid.NewGuid());
        var second = agents.Give(Guid.NewGuid());
        agents.Give(Guid.NewGuid());

        application.Cycles.Holds = true;
        var run = application.RunAsync(Quarter);
        await application.Cycles.StartedAsync(1);

        // The first runs, and the two others have their candles and wait for its place.
        await SaysAsync(application, new CycleLoad(Quarter, AwaitingCandles: 0, Queued: 2, Running: 1));

        application.Cycles.Release(first);
        await application.Cycles.StartedAsync(2);
        await SaysAsync(application, new CycleLoad(Quarter, AwaitingCandles: 0, Queued: 1, Running: 1));

        application.Cycles.Release(second);
        await application.Cycles.StartedAsync(3);
        await SaysAsync(application, new CycleLoad(Quarter, AwaitingCandles: 0, Queued: 0, Running: 1));

        // When the last of them is over, so is the run, and no close is under way.
        application.Cycles.ReleaseAll();
        await run.Within();

        Assert.Null(Load(application).Now());
    }

    [Fact]
    public async Task A_cycle_whose_candles_the_exchange_has_not_given_yet_waits_for_them()
    {
        using var application = new CyclesApplication(Quarter);
        var agents = application.Agents!;
        agents.Give(Guid.NewGuid());
        agents.Give(Guid.NewGuid());
        agents.Give(Guid.NewGuid(), symbol: "SOLUSDT");

        // To the exchange, the candle of one symbol is still in progress: its candles come one short.
        var late = true;
        application.Candles.Answer = asked =>
            asked.Symbol == "SOLUSDT" && Volatile.Read(ref late) ? StandInCandles.Through(Quarter.AddMinutes(-15), asked.Interval) : null;

        application.Cycles.Holds = true;
        var run = application.RunAsync(Quarter);

        // The two whose candles are in wait in the queue for the third to find its place in the
        // order, and the clock of the test stands still: so none starts yet.
        await SaysAsync(application, new CycleLoad(Quarter, AwaitingCandles: 1, Queued: 2, Running: 0));
        Assert.Empty(application.Cycles.Started);

        Volatile.Write(ref late, false);
        await application.Cycles.StartedAsync(3);
        await SaysAsync(application, new CycleLoad(Quarter, AwaitingCandles: 0, Queued: 0, Running: 3));

        application.Cycles.ReleaseAll();
        await run.Within();

        Assert.Null(Load(application).Now());
    }

    [Fact]
    public async Task A_run_that_is_stopped_leaves_no_close_under_way()
    {
        using var application = new CyclesApplication(Quarter);
        application.Agents!.Give(Guid.NewGuid());

        application.Cycles.Holds = true;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = application.RunAsync(Quarter, stopping.Token);
        await application.Cycles.StartedAsync(1);
        await SaysAsync(application, new CycleLoad(Quarter, AwaitingCandles: 0, Queued: 0, Running: 1));

        // The application stops the run, as it does when it stops, or loses the background work.
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Within());

        Assert.Null(Load(application).Now());
    }

    [Fact]
    public async Task A_close_at_which_no_agent_runs_is_not_under_way()
    {
        using var application = new CyclesApplication(Quarter);

        await application.RunAsync(Quarter).Within();

        Assert.Null(Load(application).Now());
    }

    private static ICycleLoad Load(CyclesApplication application) => application.Application.CycleLoad;

    /// <summary>Waits until the load is as expected: the loop of the run says it after each pass, a moment after what changed it.</summary>
    private static Task SaysAsync(CyclesApplication application, CycleLoad expected) =>
        Wait.UntilAsync(() => Load(application).Now() == expected, $"the load to be {expected}, and it is {Load(application).Now()}");
}
