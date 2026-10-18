using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using Ecnanib.Exchange.Streaming;

namespace Ecnanib.Exchange.Binance;

/// <summary>Adapts a Binance.Net <see cref="UpdateSubscription"/> to the module's handle.</summary>
internal sealed class BinanceSubscriptionHandle : StreamSubscriptionHandle
{
    private readonly UpdateSubscription _subscription;

    public BinanceSubscriptionHandle(UpdateSubscription subscription)
    {
        _subscription = subscription;
        subscription.ConnectionLost += RaiseConnectionLost;
        subscription.ConnectionRestored += RaiseConnectionRestored;
        subscription.ConnectionClosed += RaiseConnectionClosed;
        subscription.ResubscribingFailed += OnResubscribingFailed;
    }

    public override Task ReconnectAsync(CancellationToken ct) => _subscription.ReconnectAsync();

    public override async Task CloseAsync()
    {
        _subscription.ConnectionLost -= RaiseConnectionLost;
        _subscription.ConnectionRestored -= RaiseConnectionRestored;
        _subscription.ConnectionClosed -= RaiseConnectionClosed;
        _subscription.ResubscribingFailed -= OnResubscribingFailed;
        await _subscription.CloseAsync().ConfigureAwait(false);
    }

    private void OnResubscribingFailed(Error error) => RaiseResubscribeFailed();
}
