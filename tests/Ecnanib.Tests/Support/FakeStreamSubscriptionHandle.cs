using Ecnanib.Exchange.Streaming;

namespace Ecnanib.Tests.Support;

internal sealed class FakeStreamSubscriptionHandle : StreamSubscriptionHandle
{
    private int _reconnectCalls;
    private int _closeCalls;

    public int ReconnectCalls => Volatile.Read(ref _reconnectCalls);

    public int CloseCalls => Volatile.Read(ref _closeCalls);

    public bool Closed => CloseCalls > 0;

    public override Task ReconnectAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _reconnectCalls);
        return Task.CompletedTask;
    }

    public override Task CloseAsync()
    {
        Interlocked.Increment(ref _closeCalls);
        return Task.CompletedTask;
    }

    public void SimulateLost() => RaiseConnectionLost();

    public void SimulateRestored(TimeSpan offline) => RaiseConnectionRestored(offline);

    public void SimulateClosed() => RaiseConnectionClosed();

    public void SimulateResubscribeFailed() => RaiseResubscribeFailed();
}
