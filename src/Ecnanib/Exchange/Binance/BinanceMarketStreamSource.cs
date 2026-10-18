using System.Collections.Concurrent;
using CryptoExchange.Net.Objects.Sockets;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Exchange.Resilience;
using Ecnanib.Exchange.Streaming;
using Microsoft.Extensions.Options;

namespace Ecnanib.Exchange.Binance;

/// <summary>Keyless Binance socket subscriptions and the REST polls behind the market streams (EX-03, EX-06).</summary>
internal sealed class BinanceMarketStreamSource : IMarketStreamSource
{
    private readonly IBinanceClientRegistry _registry;
    private readonly ExchangeRetryPolicy _retry;
    private readonly IOptions<ExchangeOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BinanceMarketStreamSource> _logger;
    private readonly ConcurrentDictionary<(ExchangeEnvironment, MarketType), BinancePublicMarketData> _public = new();

    public BinanceMarketStreamSource(IBinanceClientRegistry registry, ExchangeRetryPolicy retry, IOptions<ExchangeOptions> options, TimeProvider time, ILogger<BinanceMarketStreamSource> logger)
    {
        _registry = registry;
        _retry = retry;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<StreamSubscriptionHandle> SubscribeCandlesAsync(MarketKey key, CandleInterval interval, Action<Candle> onUpdate, CancellationToken ct)
    {
        var socket = _registry.GetPublicSocket(key.Environment);
        var klineInterval = BinanceMappers.ToKlineInterval(interval);
        var result = key.Market == MarketType.Spot
            ? await socket.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(key.Symbol, klineInterval,
                e => Safe(() => onUpdate(BinanceMappers.ToCandle(e.Data, interval)), "kline"), ct).ConfigureAwait(false)
            : await socket.UsdFuturesApi.ExchangeData.SubscribeToKlineUpdatesAsync(key.Symbol, klineInterval,
                e => Safe(() => onUpdate(BinanceMappers.ToCandle(e.Data, interval)), "kline"), false, false, ct).ConfigureAwait(false);
        return Wrap(result, "subscribe klines", key.Market);
    }

    public async Task<StreamSubscriptionHandle> SubscribePriceAsync(MarketKey key, Action<PriceTick> onTick, CancellationToken ct)
    {
        var socket = _registry.GetPublicSocket(key.Environment);
        var result = key.Market == MarketType.Spot
            ? await socket.SpotApi.ExchangeData.SubscribeToMiniTickerUpdatesAsync(key.Symbol,
                e => Safe(() => onTick(BinanceMappers.ToPriceTick(e.Data, _time.GetUtcNow())), "mini ticker"), ct).ConfigureAwait(false)
            : await socket.UsdFuturesApi.ExchangeData.SubscribeToMiniTickerUpdatesAsync(key.Symbol,
                e => Safe(() => onTick(BinanceMappers.ToPriceTick(e.Data, _time.GetUtcNow())), "mini ticker"), ct).ConfigureAwait(false);
        return Wrap(result, "subscribe price", key.Market);
    }

    public async Task<StreamSubscriptionHandle> SubscribeMarkPriceAsync(MarketKey key, Action<MarkPrice> onMark, CancellationToken ct)
    {
        if (key.Market != MarketType.UsdFutures)
            throw new NotSupportedException("Mark price streams exist only on USD-M futures.");

        var socket = _registry.GetPublicSocket(key.Environment);
        var result = await socket.UsdFuturesApi.ExchangeData.SubscribeToMarkPriceUpdatesAsync(key.Symbol, _options.Value.Streams.MarkPriceUpdateIntervalMs,
            e => Safe(() => onMark(BinanceMappers.ToMarkPrice(e.Data, _time.GetUtcNow())), "mark price"), ct).ConfigureAwait(false);
        return Wrap(result, "subscribe mark price", key.Market);
    }

    public Task<IReadOnlyList<Candle>> PollCandlesAsync(MarketKey key, CandleInterval interval, DateTimeOffset? fromOpenTime, int limit, CancellationToken ct) =>
        Public(key).GetCandlesAsync(key.Symbol, interval, fromOpenTime, null, limit, ct);

    public Task<PriceTick> PollPriceAsync(MarketKey key, CancellationToken ct) => Public(key).GetPriceAsync(key.Symbol, ct);

    public Task<MarkPrice> PollMarkPriceAsync(MarketKey key, CancellationToken ct) => Public(key).GetMarkPriceAsync(key.Symbol, ct);

    private BinancePublicMarketData Public(MarketKey key) =>
        _public.GetOrAdd((key.Environment, key.Market), k => new BinancePublicMarketData(k.Item1, k.Item2, _registry, _retry, _time));

    private static StreamSubscriptionHandle Wrap(CryptoExchange.Net.Objects.WebSocketResult<UpdateSubscription> result, string operation, MarketType market) =>
        new BinanceSubscriptionHandle(BinanceErrorTranslator.EnsureSuccess(result, operation, market));

    private void Safe(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle a {What} stream message", what);
        }
    }
}
