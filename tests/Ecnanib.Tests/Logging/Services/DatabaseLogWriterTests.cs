using Ecnanib.Logging.Services;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Ecnanib.Tests.Logging.Services;

/// <summary>Batching, failure handling and the shutdown drain of the writer, without a database (FR-LOG-01, FR-LOG-08).</summary>
public class DatabaseLogWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WritesAFullBatchAtOnceAndAPartialOneAfterTheFlushInterval()
    {
        var h = new Harness(batchSize: 3);
        h.Emit("1");
        h.Emit("2");
        h.Emit("3");

        await h.Writer.StartAsync(Ct);
        await Wait.UntilAsync(() => h.Store.Calls == 1, "full batch");
        Assert.Equal(["1", "2", "3"], h.Store.Batches[0].Select(e => e.Message));
        Assert.Equal("Ecnanib.Tests", h.Store.Batches[0][0].Component);

        h.Emit("4");
        await Wait.UntilAsync(() => h.Time.ActiveTimerCount == 1, "lingering");
        Assert.Equal(1, h.Store.Calls);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => h.Store.Calls == 2, "partial batch");
        Assert.Equal(["4"], h.Store.Batches[1].Select(e => e.Message));
        Assert.Empty(h.Warnings);

        await h.Writer.StopAsync(Ct);
    }

    [Fact]
    public async Task DrainsPendingAndQueuedEntriesOnStop()
    {
        var h = new Harness(batchSize: 3);
        h.Emit("a");
        await h.Writer.StartAsync(Ct);
        await Wait.UntilAsync(() => h.Time.ActiveTimerCount == 1, "lingering");
        h.Emit("b");

        await h.Writer.StopAsync(Ct);

        Assert.Equal(["a", "b"], h.Store.Entries.Select(e => e.Message));
        Assert.Equal(1, h.Store.Calls);
        Assert.Empty(h.Warnings);
    }

    [Fact]
    public async Task AFailingStoreIsLoggedAtWarningOnceAMinuteRetriedWithBackoffAndNeverThrown()
    {
        var h = new Harness(batchSize: 3);
        h.Store.Failing = true;
        h.Emit("1");
        h.Emit("2");
        h.Emit("3");
        await h.Writer.StartAsync(Ct);
        await Wait.UntilAsync(() => h.Warnings.Any(), "first warning");
        Assert.Equal(1, h.Store.Calls);
        Assert.Contains("Could not write 3 log entries to the database (attempt 1)", Assert.Single(h.Warnings));

        // Retries at 2, 6, 14, 30 and 62 seconds: one warning at the start, the next once a minute has passed.
        var calls = 1;
        foreach (var delay in new[] { 2, 4, 8, 16, 32 })
        {
            await h.AdvanceAsync(TimeSpan.FromSeconds(delay));
            calls++;
            await Wait.UntilAsync(() => h.Store.Calls == calls, $"attempt {calls}");
        }

        await Wait.UntilAsync(() => h.Warnings.Count() == 2, "second warning");
        Assert.Contains("(attempt 6)", h.Warnings.Last());
        Assert.DoesNotContain(h.Logs.Entries, e => e.Level >= LogLevel.Error);

        h.Store.Failing = false;
        await h.AdvanceAsync(DatabaseLogWriter.MaxRetryDelay);
        await Wait.UntilAsync(() => h.Logs.Entries.Any(e => e.Level == LogLevel.Information && e.Message == "Database log writes resumed after 6 failed attempt(s); 3 log entries written"), "resumed");
        Assert.Equal(7, h.Store.Calls);
        Assert.Equal(["1", "2", "3"], h.Store.Entries.Select(e => e.Message));
        Assert.Equal(2, h.Warnings.Count());

        await h.Writer.StopAsync(Ct);
    }

    [Fact]
    public async Task GivesUpOnABatchAfterTheRetryTimeLimitAndKeepsGoing()
    {
        var h = new Harness(batchSize: 3);
        h.Store.Failing = true;
        h.Emit("lost");
        await h.Writer.StartAsync(Ct);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        var calls = 1;
        await Wait.UntilAsync(() => h.Store.Calls == calls, "first attempt");

        var delay = DatabaseLogWriter.InitialRetryDelay;
        while (true)
        {
            // The writer sleeps on the clock once the attempt's logging is done.
            await Wait.UntilAsync(() => h.Time.ActiveTimerCount >= 1, "retry delay");
            if (h.Logs.Entries.Any(e => e.Message.StartsWith("Giving up on 1 log entries", StringComparison.Ordinal)))
                break;
            Assert.True(calls < 30, "the writer never gave up");
            h.Time.Advance(delay);
            calls++;
            await Wait.UntilAsync(() => h.Store.Calls == calls, $"attempt {calls}");
            delay = delay * 2 > DatabaseLogWriter.MaxRetryDelay ? DatabaseLogWriter.MaxRetryDelay : delay * 2;
        }

        Assert.True(h.Time.GetUtcNow() - h.Started >= DatabaseLogWriter.RetryTimeLimit);
        h.Store.Failing = false;
        h.Emit("later");
        await h.AdvanceAsync(delay);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => h.Logs.Entries.Any(e => e.Level == LogLevel.Information && e.Message.StartsWith("Database log writes resumed", StringComparison.Ordinal)), "resumed");

        Assert.Equal(calls + 1, h.Store.Calls);
        Assert.Equal(["later"], h.Store.Entries.Select(e => e.Message));
        await h.Writer.StopAsync(Ct);
    }

    [Fact]
    public async Task ReportsDroppedEventsAfterTheNextSuccessfulWrite()
    {
        var h = new Harness(batchSize: 3, capacity: 2);
        h.Emit("1");
        h.Emit("2");
        h.Emit("3");
        Assert.Equal(1, h.Sink.Dropped);

        await h.Writer.StartAsync(Ct);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => h.Warnings.Any(), "dropped count reported");

        Assert.Equal(["1", "2"], h.Store.Entries.Select(e => e.Message));
        Assert.Equal("1 log entries were dropped because the database queue was full", Assert.Single(h.Warnings));
        Assert.Equal(0, h.Sink.Dropped);
        await h.Writer.StopAsync(Ct);
    }

    [Fact]
    public async Task ItsOwnLogsAreSuppressedFromTheSink()
    {
        var sink = new DatabaseLogSink(capacity: 100);
        var observed = new CollectingLogEventSink();
        var serilog = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).WriteTo.Sink(observed).CreateLogger();
        using var factory = new SerilogLoggerFactory(serilog, dispose: true);
        var store = new FakeLogEntryStore { Failing = true };
        var time = new FakeTimeProvider();
        var writer = new DatabaseLogWriter(sink, store, time, factory.CreateLogger<DatabaseLogWriter>(), 3, TimeSpan.FromSeconds(1));
        factory.CreateLogger("Ecnanib.Tests").LogInformation("application event");
        Assert.Equal(1, sink.Reader.Count);

        await writer.StartAsync(Ct);
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "lingering");
        time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => store.Calls == 1, "failed attempt");
        await Wait.UntilAsync(() => observed.Events.Any(e => e.Level == LogEventLevel.Warning), "warning on the console");

        Assert.Equal(0, sink.Reader.Count);
        store.Failing = false;
        await Wait.UntilAsync(() => time.ActiveTimerCount == 1, "retry delay");
        time.Advance(DatabaseLogWriter.InitialRetryDelay);
        await Wait.UntilAsync(() => store.Calls == 2, "retry");
        Assert.Equal(["application event"], store.Entries.Select(e => e.Message));
        Assert.Equal(0, sink.Reader.Count);
        await writer.StopAsync(Ct);
    }

    private sealed class Harness
    {
        public Harness(int batchSize = 3, int capacity = 100)
        {
            Sink = new DatabaseLogSink(capacity);
            Factory = TestLogging.CreateFactory(Logs);
            Started = Time.GetUtcNow();
            Writer = new DatabaseLogWriter(Sink, Store, Time, Factory.CreateLogger<DatabaseLogWriter>(), batchSize, TimeSpan.FromSeconds(1));
        }

        public DatabaseLogSink Sink { get; }

        public FakeLogEntryStore Store { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public TestLoggerProvider Logs { get; } = new();

        public ILoggerFactory Factory { get; }

        public DatabaseLogWriter Writer { get; }

        public DateTimeOffset Started { get; }

        public IEnumerable<string> Warnings => Logs.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public void Emit(string message) => Sink.Emit(SinkLevelFilterTests.Event(LogEventLevel.Information, "Ecnanib.Tests", message));

        /// <summary>Waits for the writer to be sleeping on the fake clock, then moves it.</summary>
        public async Task AdvanceAsync(TimeSpan by)
        {
            await Wait.UntilAsync(() => Time.ActiveTimerCount >= 1, "a timer to advance");
            Time.Advance(by);
        }
    }
}
