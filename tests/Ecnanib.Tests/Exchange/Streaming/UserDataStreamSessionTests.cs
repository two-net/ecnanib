using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Exchange.Streaming;
using Ecnanib.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ecnanib.Tests.Exchange.Streaming;

public class UserDataStreamSessionTests
{
    private static readonly ExchangeKeyHandle Key = new("key-1", ExchangeEnvironment.Testnet, new ExchangeCredentials("abcdefgh12345", "secret"));

    private sealed class Harness : IAsyncDisposable
    {
        public FakeTimeProvider Time { get; } = new();
        public FakeUserDataSource Source { get; } = new();
        public UserDataStreamSession Session { get; }
        public List<UserDataEvent> Events { get; } = [];

        public Harness(bool start = true)
        {
            var options = new ExchangeStreamOptions { UserDataPollIntervalSeconds = 10, ReconnectInitialDelaySeconds = 2, ReconnectMaxDelaySeconds = 60 };
            Session = new UserDataStreamSession(Key, MarketType.UsdFutures, Source, options, Time, NullLogger.Instance);
            Session.AddHandler((e, _) => { lock (Events) Events.Add(e); return ValueTask.CompletedTask; });
            if (start)
                Session.Start();
        }

        public int Count<T>() where T : UserDataEvent { lock (Events) return Events.OfType<T>().Count(); }

        public async Task ConnectedAsync() => await Wait.UntilAsync(() => Session.Status.Health == StreamHealth.Connected, "connected");

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    [Fact]
    public async Task ForwardsStreamEvents()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();
        Assert.Equal("userdata:testnet:usdfutures:key-1", h.Session.Name);

        h.Source.OnEvent!(new UserDataEvent.LeverageUpdate("key-1", MarketType.UsdFutures, h.Time.GetUtcNow(), "BTCUSDT", 5));
        await Wait.UntilAsync(() => h.Count<UserDataEvent.LeverageUpdate>() == 1);

        Assert.Equal(0, h.Source.SnapshotCalls);
        Assert.NotNull(h.Session.Status.LastMessageAt);
    }

    [Fact]
    public async Task LostConnectionPollsSnapshotsUntilRestored()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();

        h.Source.Handles[0].SimulateLost();
        await Wait.UntilAsync(() => h.Count<UserDataEvent.StreamLost>() == 1, "stream lost event");
        await Wait.UntilAsync(() => h.Session.Status.Health == StreamHealth.Polling);
        await Wait.UntilAsync(() => h.Source.SnapshotCalls == 1, "immediate snapshot");
        await Wait.SettleAsync();
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => h.Source.SnapshotCalls == 2, "periodic snapshot");
        await Wait.UntilAsync(() => h.Count<UserDataEvent.Snapshot>() == 2);

        h.Source.Handles[0].SimulateRestored(TimeSpan.FromSeconds(12));
        await Wait.UntilAsync(() => h.Count<UserDataEvent.StreamRestored>() == 1, "restored event");
        await Wait.UntilAsync(() => h.Source.SnapshotCalls == 3, "snapshot after restore");
        await Wait.UntilAsync(() => h.Session.Status.Health == StreamHealth.Connected);
        await Wait.SettleAsync();
        h.Time.Advance(TimeSpan.FromSeconds(30));
        await Wait.SettleAsync();

        Assert.Equal(3, h.Source.SnapshotCalls);
        lock (h.Events)
        {
            Assert.Equal(TimeSpan.FromSeconds(12), h.Events.OfType<UserDataEvent.StreamRestored>().Single().Offline);
        }
    }

    [Fact]
    public async Task TerminatedStreamIsRecreatedAfterBackoff()
    {
        await using var h = new Harness();
        await h.ConnectedAsync();

        h.Source.OnTerminated!();
        await Wait.UntilAsync(() => h.Source.Handles[0].Closed, "old handle closed");
        await Wait.UntilAsync(() => h.Count<UserDataEvent.StreamLost>() == 1);
        await Wait.UntilAsync(() => h.Source.SnapshotCalls == 1, "snapshot while terminated");
        await Wait.SettleAsync();
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await Wait.UntilAsync(() => h.Source.SubscribeCalls == 2, "resubscribed");
        await Wait.UntilAsync(() => h.Count<UserDataEvent.StreamRestored>() == 1, "restored after resubscribe");
        await Wait.UntilAsync(() => h.Source.SnapshotCalls == 2, "snapshot after resubscribe");
        await Wait.UntilAsync(() => h.Session.Status.Health == StreamHealth.Connected);
        await Wait.SettleAsync();
        Assert.Equal(1, h.Count<UserDataEvent.StreamLost>());
    }

    [Fact]
    public async Task SubscribeFailureIsReportedAndPolled()
    {
        await using var h = new Harness(start: false);
        h.Source.SubscribeError = new ExchangeAuthException("bad key");
        h.Session.Start();

        await Wait.UntilAsync(() => h.Count<UserDataEvent.StreamLost>() == 1);
        await Wait.UntilAsync(() => h.Source.SnapshotCalls == 1);
        Assert.Equal(StreamHealth.Polling, h.Session.Status.Health);
        Assert.Equal("bad key", h.Session.Status.LastError);

        h.Source.SnapshotError = new ExchangeUnavailableException("rest down");
        await Wait.SettleAsync();
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => h.Session.Status.Health == StreamHealth.Down);
    }

    [Fact]
    public async Task HubSharesSessionsAndStopsThemPerKey()
    {
        var source = new FakeUserDataSource();
        var hub = new UserDataStreamHub(source, Options.Create(new ExchangeOptions()), new FakeTimeProvider(), NullLoggerFactory.Instance);

        using var a = hub.Subscribe(Key, MarketType.Spot, (_, _) => ValueTask.CompletedTask);
        using var b = hub.Subscribe(Key, MarketType.Spot, (_, _) => ValueTask.CompletedTask);
        using var c = hub.Subscribe(Key, MarketType.UsdFutures, (_, _) => ValueTask.CompletedTask);
        await Wait.UntilAsync(() => source.SubscribeCalls == 2);
        Assert.Equal(2, hub.GetStatuses().Count);
        Assert.Equal(StreamHealth.Stopped, hub.GetStatus("other", MarketType.Spot).Health);

        await hub.StopSessionsAsync("key-1", TestContext.Current.CancellationToken);

        Assert.Empty(hub.GetStatuses());
        Assert.All(source.Handles, handle => Assert.True(handle.Closed));
    }
}
