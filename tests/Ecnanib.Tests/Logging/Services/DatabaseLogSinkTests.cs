using Ecnanib.Logging.Services;
using Serilog.Events;

namespace Ecnanib.Tests.Logging.Services;

public class DatabaseLogSinkTests
{
    [Fact]
    public void QueuesEventsAndCountsWhatAFullQueueDrops()
    {
        var sink = new DatabaseLogSink(capacity: 2);

        sink.Emit(SinkLevelFilterTests.Event(LogEventLevel.Information, "A", "one"));
        sink.Emit(SinkLevelFilterTests.Event(LogEventLevel.Information, "A", "two"));
        sink.Emit(SinkLevelFilterTests.Event(LogEventLevel.Information, "A", "three"));

        Assert.Equal(2, sink.Reader.Count);
        Assert.Equal(1, sink.Dropped);
        Assert.Equal(1, sink.TakeDropped());
        Assert.Equal(0, sink.Dropped);
        Assert.True(sink.Reader.TryRead(out var first));
        Assert.Equal("one", first.MessageTemplate.Text);
    }

    [Fact]
    public async Task IgnoresEventsOnASuppressedFlowAndTheFlagFollowsTheFlow()
    {
        var sink = new DatabaseLogSink(capacity: 10);

        Assert.False(DatabaseLogSink.IsSuppressed);
        using (DatabaseLogSink.Suppress())
        {
            Assert.True(DatabaseLogSink.IsSuppressed);
            sink.Emit(SinkLevelFilterTests.Event(LogEventLevel.Warning, "A", "suppressed"));
            Assert.True(await Task.Run(() => DatabaseLogSink.IsSuppressed));
        }

        Assert.False(DatabaseLogSink.IsSuppressed);
        sink.Emit(SinkLevelFilterTests.Event(LogEventLevel.Warning, "A", "kept"));

        Assert.Equal(1, sink.Reader.Count);
        Assert.Equal(0, sink.Dropped);
        Assert.True(sink.Reader.TryRead(out var kept));
        Assert.Equal("kept", kept.MessageTemplate.Text);
    }
}
