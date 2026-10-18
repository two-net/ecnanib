using Binance.Net.Interfaces.Clients;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Resilience;

namespace Ecnanib.Exchange.Binance;

/// <summary>Keyless market data for one environment and market (EX-06). Uses only unsigned endpoints.</summary>
internal sealed class BinancePublicMarketData : IPublicMarketData
{
    private const int SpotMaxKlines = 1000;
    private const int FuturesMaxKlines = 1500;

    private readonly IBinanceClientRegistry _registry;
    private readonly ExchangeRetryPolicy _retry;
    private readonly TimeProvider _time;

    public BinancePublicMarketData(ExchangeEnvironment environment, MarketType market, IBinanceClientRegistry registry, ExchangeRetryPolicy retry, TimeProvider time)
    {
        Environment = environment;
        Market = market;
        _registry = registry;
        _retry = retry;
        _time = time;
    }

    public ExchangeEnvironment Environment { get; }

    public MarketType Market { get; }

    private IBinanceRestClient Client => _registry.GetPublicRest(Environment);

    public Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct) => _retry.ExecuteAsync(async token =>
    {
        var result = Market == MarketType.Spot
            ? await Client.SpotApi.ExchangeData.GetServerTimeAsync(token).ConfigureAwait(false)
            : await Client.UsdFuturesApi.ExchangeData.GetServerTimeAsync(false, token).ConfigureAwait(false);
        return BinanceMappers.ToUtc(BinanceErrorTranslator.EnsureSuccess(result, "server time", Market));
    }, RetryProfile.Query, "server time", ct);

    public Task<ExchangeInfo> GetExchangeInfoAsync(CancellationToken ct) => _retry.ExecuteAsync(async token =>
    {
        if (Market == MarketType.Spot)
        {
            var result = await Client.SpotApi.ExchangeData.GetExchangeInfoAsync(ct: token).ConfigureAwait(false);
            var info = BinanceErrorTranslator.EnsureSuccess(result, "exchange info", Market);
            return new ExchangeInfo(
                BinanceMappers.ToUtc(info.ServerTime),
                info.Symbols.Select(BinanceMappers.ToSymbolInfo).ToArray(),
                info.RateLimits.Select(BinanceMappers.ToRateLimitRule).ToArray());
        }
        else
        {
            var result = await Client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(token).ConfigureAwait(false);
            var info = BinanceErrorTranslator.EnsureSuccess(result, "exchange info", Market);
            return new ExchangeInfo(
                BinanceMappers.ToUtc(info.ServerTime),
                info.Symbols.Select(BinanceMappers.ToSymbolInfo).ToArray(),
                info.RateLimits.Select(BinanceMappers.ToRateLimitRule).ToArray());
        }
    }, RetryProfile.Query, "exchange info", ct);

    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, CandleInterval interval, DateTimeOffset? from, DateTimeOffset? to, int? limit, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        return _retry.ExecuteAsync<IReadOnlyList<Candle>>(async token =>
        {
            var klineInterval = BinanceMappers.ToKlineInterval(interval);
            var result = Market == MarketType.Spot
                ? await Client.SpotApi.ExchangeData.GetKlinesAsync(normalized, klineInterval, from?.UtcDateTime, to?.UtcDateTime, limit, token).ConfigureAwait(false)
                : await Client.UsdFuturesApi.ExchangeData.GetKlinesAsync(normalized, klineInterval, from?.UtcDateTime, to?.UtcDateTime, limit, token).ConfigureAwait(false);
            var klines = BinanceErrorTranslator.EnsureSuccess(result, "klines", Market);
            var now = _time.GetUtcNow();
            return klines.Select(k => BinanceMappers.ToCandle(k, normalized, interval, now)).OrderBy(c => c.OpenTime).ToArray();
        }, RetryProfile.Query, "klines", ct);
    }

    public async Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, CandleInterval interval, int count, bool closedOnly, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var pageSize = Market == MarketType.Spot ? SpotMaxKlines : FuturesMaxKlines;
        var collected = new List<Candle>();
        DateTimeOffset? end = null;

        while (collected.Count < count)
        {
            var wanted = count - collected.Count;
            // The still-forming candle may be returned and dropped, so ask for one extra when it must be excluded.
            var limit = Math.Min(pageSize, closedOnly && end is null ? wanted + 1 : wanted);
            var page = await GetCandlesAsync(symbol, interval, null, end, limit, ct).ConfigureAwait(false);
            if (closedOnly)
                page = page.Where(c => c.IsClosed).ToArray();
            if (page.Count == 0)
                break;

            collected.InsertRange(0, page);
            end = page[0].OpenTime - TimeSpan.FromMilliseconds(1);
            if (page.Count < limit - (closedOnly && collected.Count == page.Count ? 1 : 0))
                break; // exchange has no older candles
        }

        return collected.Count <= count ? collected : collected.GetRange(collected.Count - count, count);
    }

    public Task<PriceTick> GetPriceAsync(string symbol, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        return _retry.ExecuteAsync(async token =>
        {
            var result = Market == MarketType.Spot
                ? await Client.SpotApi.ExchangeData.GetPriceAsync(normalized, token).ConfigureAwait(false)
                : await Client.UsdFuturesApi.ExchangeData.GetPriceAsync(normalized, token).ConfigureAwait(false);
            return BinanceMappers.ToPriceTick(BinanceErrorTranslator.EnsureSuccess(result, "price", Market), _time.GetUtcNow());
        }, RetryProfile.Query, "price", ct);
    }

    public Task<MarkPrice> GetMarkPriceAsync(string symbol, CancellationToken ct)
    {
        if (Market != MarketType.UsdFutures)
            throw new NotSupportedException("Mark price is only available on USD-M futures.");

        var normalized = NormalizeSymbol(symbol);
        return _retry.ExecuteAsync(async token =>
        {
            var result = await Client.UsdFuturesApi.ExchangeData.GetMarkPriceAsync(normalized, token).ConfigureAwait(false);
            return BinanceMappers.ToMarkPrice(BinanceErrorTranslator.EnsureSuccess(result, "mark price", Market));
        }, RetryProfile.Query, "mark price", ct);
    }

    internal static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        return symbol.Trim().ToUpperInvariant();
    }
}
