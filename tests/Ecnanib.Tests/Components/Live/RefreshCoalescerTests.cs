using Ecnanib.Components.Live;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Components.Live;

/// <summary>The live islands' refresh loop (FR-UI-04, NFR-PERF-03) under a fake clock: coalescing, the trailing read, the latency measurement and its budget.</summary>
public class RefreshCoalescerTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ABurstOfRequestsWithinTheDebounceBecomesOneReadAndOneRender()
    {
        var time = new FakeTimeProvider();
        var loads = 0;
        var renders = 0;
        var (coalescer, _) = Build(time, _ => { Interlocked.Increment(ref loads); return Task.CompletedTask; }, () => { Interlocked.Increment(ref renders); return Task.CompletedTask; });
        var loop = coalescer.RunAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 5; i++)
            coalescer.Request();
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "debounce armed");
        time.Advance(Debounce);
        await Wait.UntilAsync(() => coalescer.Refreshes == 1, "refreshed");
        await Wait.SettleAsync();

        Assert.Equal(1, loads);
        Assert.Equal(1, renders);
        Assert.Equal(Debounce, coalescer.LastLatency);
        Assert.Equal(0, time.ActiveTimerCount);
        coalescer.Complete();
        await loop;
    }

    [Fact]
    public async Task ARequestDuringAReadYieldsOneTrailingRead()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var (coalescer, _) = Build(time, async ct =>
        {
            if (Interlocked.Increment(ref loads) == 1)
                await gate.Task.WaitAsync(ct);
        }, () => Task.CompletedTask);
        var loop = coalescer.RunAsync(TestContext.Current.CancellationToken);

        coalescer.Request();
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "debounce armed");
        time.Advance(Debounce);
        await Wait.UntilAsync(() => loads == 1, "reading");
        coalescer.Request();
        gate.SetResult();
        await Wait.UntilAsync(() => coalescer.Refreshes == 1, "first refresh");
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "trailing debounce armed");
        time.Advance(Debounce);
        await Wait.UntilAsync(() => coalescer.Refreshes == 2, "trailing refresh");

        Assert.Equal(2, loads);
        coalescer.Complete();
        await loop;
    }

    [Fact]
    public async Task TheLatencyRunsFromTheFirstRequestToTheRender()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (coalescer, logs) = Build(time, ct => gate.Task.WaitAsync(ct), () => Task.CompletedTask);
        var loop = coalescer.RunAsync(TestContext.Current.CancellationToken);

        coalescer.Request();
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "debounce armed");
        coalescer.Request();
        time.Advance(Debounce);
        await Wait.UntilAsync(() => time.ActiveTimerCount == 0, "reading");
        // The read takes 100 ms of fake time; the latency counts from the first request, the debounce included.
        time.Advance(TimeSpan.FromMilliseconds(100));
        gate.SetResult();
        await Wait.UntilAsync(() => coalescer.Refreshes == 1, "refreshed");

        Assert.Equal(TimeSpan.FromMilliseconds(350), coalescer.LastLatency);
        var entry = Assert.Single(logs.Entries, e => e.Message.Contains("reflected an event", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains("DashboardLive", entry.Message, StringComparison.Ordinal);
        coalescer.Complete();
        await loop;
    }

    [Fact]
    public async Task ARefreshOverTheBudgetIsAWarning()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var (coalescer, logs) = Build(time, async ct =>
        {
            if (Interlocked.Increment(ref loads) == 1)
                await gate.Task.WaitAsync(ct);
        }, () => Task.CompletedTask);
        var loop = coalescer.RunAsync(TestContext.Current.CancellationToken);

        coalescer.Request();
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "debounce armed");
        time.Advance(Debounce);
        await Wait.UntilAsync(() => loads == 1, "reading");
        time.Advance(TimeSpan.FromSeconds(2.5));
        gate.SetResult();
        await Wait.UntilAsync(() => coalescer.Refreshes == 1, "slow refresh");

        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("NFR-PERF-03", warning.Message, StringComparison.Ordinal);
        Assert.Contains("DashboardLive", warning.Message, StringComparison.Ordinal);
        Assert.Equal(Debounce + TimeSpan.FromSeconds(2.5), coalescer.LastLatency);

        // A quick refresh after it is within the budget again.
        coalescer.Request();
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "debounce armed again");
        time.Advance(Debounce);
        await Wait.UntilAsync(() => coalescer.Refreshes == 2, "quick refresh");
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(Debounce, coalescer.LastLatency);
        coalescer.Complete();
        await loop;
    }

    [Fact]
    public async Task AFailingReadIsAWarningAndTheNextRequestTriesAgain()
    {
        var time = new FakeTimeProvider();
        var loads = 0;
        var (coalescer, logs) = Build(time, _ => Interlocked.Increment(ref loads) == 1 ? throw new InvalidOperationException("database down") : Task.CompletedTask, () => Task.CompletedTask);
        var loop = coalescer.RunAsync(TestContext.Current.CancellationToken);

        coalescer.Request();
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "debounce armed");
        time.Advance(Debounce);
        await Wait.UntilAsync(() => logs.Entries.Any(e => e.Level == LogLevel.Warning), "failure logged");
        Assert.Equal(0, coalescer.Refreshes);
        Assert.Contains("could not refresh", Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning).Message, StringComparison.Ordinal);

        coalescer.Request();
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "debounce armed again");
        time.Advance(Debounce);
        await Wait.UntilAsync(() => coalescer.Refreshes == 1, "refreshed");
        Assert.Equal(2, loads);
        coalescer.Complete();
        await loop;
    }

    [Fact]
    public async Task CompletionAndCancellationEndTheLoop()
    {
        var time = new FakeTimeProvider();
        var (completed, _) = Build(time, _ => Task.CompletedTask, () => Task.CompletedTask);
        var loop = completed.RunAsync(TestContext.Current.CancellationToken);
        completed.Complete();
        await loop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var (cancelled, _) = Build(time, _ => Task.CompletedTask, () => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        var running = cancelled.RunAsync(cts.Token);
        cancelled.Request();
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "debounce armed");
        await cts.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, cancelled.Refreshes);
    }

    private static (RefreshCoalescer Coalescer, TestLoggerProvider Logs) Build(FakeTimeProvider time, Func<CancellationToken, Task> load, Func<Task> render)
    {
        var logs = new TestLoggerProvider();
        var logger = TestLogging.CreateFactory(logs).CreateLogger("Ecnanib.Components.Live.DashboardLive");
        return (new RefreshCoalescer(load, render, time, logger, "DashboardLive", Debounce, Budget), logs);
    }
}
