using System.Collections.Concurrent;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>
/// Stands in for the Binance factory. Pings and permission reads are scripted; the per-key-id cache mirrors the real
/// factory's rule that an id reused with other credentials or environment throws until released.
/// </summary>
internal sealed class FakeExchangeClientFactory : IExchangeClientFactory
{
    private readonly ConcurrentDictionary<string, (ExchangeEnvironment Environment, string ApiKey)> _cached = new();

    /// <summary>Called for every ping; throw to script a failure. Defaults to success.</summary>
    public Func<ExchangeKeyHandle, MarketType, CancellationToken, Task>? OnPing { get; set; }

    /// <summary>Called for permission reads on Production; off Production the fake answers <see cref="ApiKeyPermissions.Unverified"/> without calling it.</summary>
    public Func<ExchangeKeyHandle, ApiKeyPermissions>? OnPermissions { get; set; }

    public static ApiKeyPermissions TradingOnly { get; } = new(true, true, true, true, false, true, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public static ApiKeyPermissions WithWithdrawals { get; } = TradingOnly with { EnableWithdrawals = true };

    public List<(ExchangeKeyHandle Handle, MarketType Market)> Pings { get; } = [];

    public List<ExchangeKeyHandle> PermissionReads { get; } = [];

    public List<string> Released { get; } = [];

    public IReadOnlyCollection<string> CachedKeyIds => _cached.Keys.ToArray();

    /// <summary>Called for exchange-info reads; throw to script an outage. Unset, every public-data call is unsupported, as before the agents work.</summary>
    public Func<ExchangeEnvironment, MarketType, ExchangeInfo>? OnExchangeInfo { get; set; }

    /// <summary>Called for recent-candle reads (symbol, interval, count); unset, the call is unsupported.</summary>
    public Func<string, CandleInterval, int, IReadOnlyList<Candle>>? OnRecentCandles { get; set; }

    /// <summary>Called for mark-price reads (symbol); unset, the call is unsupported, which the loader tolerates.</summary>
    public Func<string, MarkPrice>? OnMarkPrice { get; set; }

    /// <summary>Every recent-candle read, in order, so a test can assert which series were asked for.</summary>
    public List<(ExchangeEnvironment Environment, MarketType Market, string Symbol, CandleInterval Interval, int Count)> RecentCandleReads { get; } = [];

    public List<(ExchangeEnvironment Environment, MarketType Market)> ExchangeInfoReads { get; } = [];

    /// <summary>Exchange information listing the given symbols as trading, with permissive filters.</summary>
    public static ExchangeInfo Listing(params string[] symbols) => new(DateTimeOffset.UnixEpoch,
        symbols.Select(s => new SymbolInfo(s, s[..^4], s[^4..], true, "TRADING", 2, 3, true,
            new SymbolFilters(0.01m, 0.01m, 1_000_000m, 0.001m, 0.001m, 1000m, null, null, null, 5m, null, true))).ToList(), []);

    public IPublicMarketData GetPublicData(ExchangeEnvironment environment, MarketType market) => new FakePublicMarketData(this, environment, market);

    public ISpotAccountClient GetSpotClient(ExchangeKeyHandle key) => Client(key, MarketType.Spot);

    public IFuturesAccountClient GetFuturesClient(ExchangeKeyHandle key) => Client(key, MarketType.UsdFutures);

    public Task ReleaseAsync(string keyId, CancellationToken ct)
    {
        lock (Released)
            Released.Add(keyId);
        _cached.TryRemove(keyId, out _);
        return Task.CompletedTask;
    }

    private FakeExchangeAccountClient Client(ExchangeKeyHandle key, MarketType market)
    {
        var entry = _cached.GetOrAdd(key.KeyId, _ => (key.Environment, key.Credentials.ApiKey));
        if (entry.Environment != key.Environment || !string.Equals(entry.ApiKey, key.Credentials.ApiKey, StringComparison.Ordinal))
            throw new InvalidOperationException($"Key {key.KeyId} is cached with other credentials. Release the key before rotating it.");
        return new FakeExchangeAccountClient(this, key, market);
    }

    private Task PingAsync(ExchangeKeyHandle key, MarketType market, CancellationToken ct)
    {
        lock (Pings)
            Pings.Add((key, market));
        return OnPing?.Invoke(key, market, ct) ?? Task.CompletedTask;
    }

    private ApiKeyPermissions Permissions(ExchangeKeyHandle key)
    {
        lock (PermissionReads)
            PermissionReads.Add(key);
        if (key.Environment != ExchangeEnvironment.Production)
            return ApiKeyPermissions.Unverified;
        return OnPermissions?.Invoke(key) ?? TradingOnly;
    }

    private sealed class FakePublicMarketData(FakeExchangeClientFactory owner, ExchangeEnvironment environment, MarketType market) : IPublicMarketData
    {
        public ExchangeEnvironment Environment => environment;

        public MarketType Market => market;

        public Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ExchangeInfo> GetExchangeInfoAsync(CancellationToken ct)
        {
            lock (owner.ExchangeInfoReads)
                owner.ExchangeInfoReads.Add((environment, market));
            return Task.FromResult(owner.OnExchangeInfo?.Invoke(environment, market) ?? throw new NotSupportedException("No exchange information is scripted."));
        }

        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, CandleInterval interval, DateTimeOffset? from, DateTimeOffset? to, int? limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, CandleInterval interval, int count, bool closedOnly, CancellationToken ct)
        {
            lock (owner.RecentCandleReads)
                owner.RecentCandleReads.Add((environment, market, symbol, interval, count));
            return Task.FromResult(owner.OnRecentCandles?.Invoke(symbol, interval, count) ?? throw new NotSupportedException("No candles are scripted."));
        }

        public Task<PriceTick> GetPriceAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();

        public Task<MarkPrice> GetMarkPriceAsync(string symbol, CancellationToken ct) =>
            Task.FromResult(owner.OnMarkPrice?.Invoke(symbol) ?? throw new NotSupportedException("No mark price is scripted."));
    }

    private sealed class FakeExchangeAccountClient(FakeExchangeClientFactory owner, ExchangeKeyHandle key, MarketType market) : ISpotAccountClient, IFuturesAccountClient
    {
        public string KeyId => key.KeyId;

        public ExchangeEnvironment Environment => key.Environment;

        public MarketType Market => market;

        public Task PingAsync(CancellationToken ct) => owner.PingAsync(key, market, ct);

        public Task<ApiKeyPermissions> GetApiKeyPermissionsAsync(CancellationToken ct) => Task.FromResult(owner.Permissions(key));

        public Task<IReadOnlyList<AssetBalance>> GetBalancesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ExchangeOrder> PlaceOrderAsync(OrderRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExchangeOrder> GetOrderAsync(string symbol, OrderRef order, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExchangeOrder> CancelOrderAsync(string symbol, OrderRef order, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ExchangeOrder>> CancelAllOrdersAsync(string symbol, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<UserTrade>> GetTradesAsync(string symbol, DateTimeOffset? from, long? fromTradeId, int? limit, CancellationToken ct) => throw new NotSupportedException();

        public IDisposable SubscribeUserData(Func<UserDataEvent, CancellationToken, ValueTask> handler) => throw new NotSupportedException();

        public StreamStatus UserDataStatus => throw new NotSupportedException();

        public Task<OcoOrderList> PlaceOcoAsync(SpotOcoRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<OcoOrderList> CancelOcoAsync(string symbol, string listClientOrderId, CancellationToken ct) => throw new NotSupportedException();

        public Task<OcoOrderList> GetOcoAsync(string listClientOrderId, CancellationToken ct) => throw new NotSupportedException();

        public Task<FuturesAccountSummary> GetAccountSummaryAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<FuturesPosition>> GetPositionsAsync(string? symbol, CancellationToken ct) => throw new NotSupportedException();

        public Task<PositionMode> GetPositionModeAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task SetPositionModeAsync(PositionMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> SetLeverageAsync(string symbol, int leverage, CancellationToken ct) => throw new NotSupportedException();

        public Task SetMarginTypeAsync(string symbol, MarginType marginType, CancellationToken ct) => throw new NotSupportedException();
    }
}
