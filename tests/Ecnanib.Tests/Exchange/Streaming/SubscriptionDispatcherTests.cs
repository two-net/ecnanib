using Ecnanib.Exchange.Streaming;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Exchange.Streaming;

public class SubscriptionDispatcherTests
{
    [Fact]
    public async Task DeliversInOrderToAllHandlers()
    {
        await using var dispatcher = new SubscriptionDispatcher<int>("test", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var first = new List<int>();
        var second = new List<int>();
        using var a = dispatcher.AddHandler((i, _) => { first.Add(i); return ValueTask.CompletedTask; });
        using var b = dispatcher.AddHandler(async (i, ct) => { await Task.Delay(1, ct); second.Add(i); });

        foreach (var i in Enumerable.Range(1, 5))
            dispatcher.TryPublish(i);

        await Wait.UntilAsync(() => second.Count == 5);
        Assert.Equal([1, 2, 3, 4, 5], first);
        Assert.Equal([1, 2, 3, 4, 5], second);
        Assert.Equal(2, dispatcher.HandlerCount);
    }

    [Fact]
    public async Task ThrowingHandlerIsLoggedAndOthersContinue()
    {
        var logs = new TestLoggerProvider();
        await using var dispatcher = new SubscriptionDispatcher<int>("test", TestLogging.CreateFactory(logs).CreateLogger("test"));
        var received = new List<int>();
        using var bad = dispatcher.AddHandler((_, _) => throw new InvalidOperationException("consumer bug"));
        using var good = dispatcher.AddHandler((i, _) => { received.Add(i); return ValueTask.CompletedTask; });

        dispatcher.TryPublish(1);
        dispatcher.TryPublish(2);

        await Wait.UntilAsync(() => received.Count == 2);
        Assert.Equal(2, logs.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains("handler of stream test")));
    }

    [Fact]
    public async Task DisposedRegistrationStopsReceiving()
    {
        await using var dispatcher = new SubscriptionDispatcher<int>("test", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var received = new List<int>();
        var registration = dispatcher.AddHandler((i, _) => { received.Add(i); return ValueTask.CompletedTask; });
        dispatcher.TryPublish(1);
        await Wait.UntilAsync(() => received.Count == 1);

        registration.Dispose();
        registration.Dispose();
        dispatcher.TryPublish(2);
        await Wait.SettleAsync();

        Assert.Equal([1], received);
        Assert.Equal(0, dispatcher.HandlerCount);
    }
}
