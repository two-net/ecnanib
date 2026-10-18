using System.Collections.Concurrent;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Resilience;
using Ecnanib.Exchange.Streaming;

namespace Ecnanib.Exchange.Binance;

internal sealed class BinanceClientFactory : IExchangeClientFactory
{
    private readonly IBinanceClientRegistry _registry;
    private readonly ExchangeRetryPolicy _retry;
    private readonly IUserDataStreams _userData;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(ExchangeEnvironment, MarketType), IPublicMarketData> _public = new();
    private readonly ConcurrentDictionary<string, BinanceSpotAccountClient> _spot = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BinanceFuturesAccountClient> _futures = new(StringComparer.Ordinal);

    public BinanceClientFactory(IBinanceClientRegistry registry, ExchangeRetryPolicy retry, IUserDataStreams userData, TimeProvider time)
    {
        _registry = registry;
        _retry = retry;
        _userData = userData;
        _time = time;
    }

    public IPublicMarketData GetPublicData(ExchangeEnvironment environment, MarketType market) =>
        _public.GetOrAdd((environment, market), k => new BinancePublicMarketData(k.Item1, k.Item2, _registry, _retry, _time));

    public ISpotAccountClient GetSpotClient(ExchangeKeyHandle key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var client = _spot.GetOrAdd(key.KeyId, _ => new BinanceSpotAccountClient(key, _registry, _retry, _userData, _time));
        EnsureSameKey(client.Key, key);
        return client;
    }

    public IFuturesAccountClient GetFuturesClient(ExchangeKeyHandle key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var client = _futures.GetOrAdd(key.KeyId, _ => new BinanceFuturesAccountClient(key, _registry, _retry, _userData, _time));
        EnsureSameKey(client.Key, key);
        return client;
    }

    public async Task ReleaseAsync(string keyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        _spot.TryRemove(keyId, out _);
        _futures.TryRemove(keyId, out _);
        await _userData.StopSessionsAsync(keyId, ct).ConfigureAwait(false);
        await _registry.ReleaseKeyAsync(keyId, ct).ConfigureAwait(false);
    }

    private static void EnsureSameKey(ExchangeKeyHandle cached, ExchangeKeyHandle requested)
    {
        if (cached.Environment != requested.Environment)
        {
            throw new InvalidOperationException(
                $"Exchange key {requested.KeyId} is in use for {cached.Environment} but was requested for {requested.Environment}. Release the key first.");
        }

        if (!string.Equals(cached.Credentials.ApiKey, requested.Credentials.ApiKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Exchange key {requested.KeyId} is in use with different credentials ({cached.Credentials.MaskedApiKey}). Release the key before rotating it.");
        }
    }
}
