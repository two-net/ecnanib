using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Resilience;
using Ecnanib.Exchange.Streaming;

namespace Ecnanib.Exchange.Binance;

/// <summary>Shared plumbing of the authenticated clients: keyed REST access, retries, error translation and user-data streams.</summary>
internal abstract class BinanceAccountClientBase : IExchangeAccountClient
{
    private readonly IBinanceClientRegistry _registry;
    private readonly ExchangeRetryPolicy _retry;
    private readonly IUserDataStreams _userData;
    private readonly TimeProvider _time;

    protected BinanceAccountClientBase(
        ExchangeKeyHandle key,
        MarketType market,
        IBinanceClientRegistry registry,
        ExchangeRetryPolicy retry,
        IUserDataStreams userData,
        TimeProvider time)
    {
        Key = key;
        Market = market;
        _registry = registry;
        _retry = retry;
        _userData = userData;
        _time = time;
    }

    public ExchangeKeyHandle Key { get; }

    public string KeyId => Key.KeyId;

    public ExchangeEnvironment Environment => Key.Environment;

    public MarketType Market { get; }

    protected IBinanceRestClient Rest => _registry.GetKeyedRest(Key);

    protected DateTimeOffset Now => _time.GetUtcNow();

    public abstract Task PingAsync(CancellationToken ct);

    public abstract Task<IReadOnlyList<AssetBalance>> GetBalancesAsync(CancellationToken ct);

    public abstract Task<ExchangeOrder> PlaceOrderAsync(OrderRequest request, CancellationToken ct);

    public abstract Task<ExchangeOrder> GetOrderAsync(string symbol, OrderRef order, CancellationToken ct);

    public abstract Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct);

    public abstract Task<ExchangeOrder> CancelOrderAsync(string symbol, OrderRef order, CancellationToken ct);

    public abstract Task<IReadOnlyList<ExchangeOrder>> CancelAllOrdersAsync(string symbol, CancellationToken ct);

    public abstract Task<IReadOnlyList<UserTrade>> GetTradesAsync(string symbol, DateTimeOffset? from, long? fromTradeId, int? limit, CancellationToken ct);

    /// <summary>The permissions endpoint is a production-only <c>sapi</c> call; test environments report unverified.</summary>
    public Task<ApiKeyPermissions> GetApiKeyPermissionsAsync(CancellationToken ct)
    {
        if (Environment != ExchangeEnvironment.Production)
            return Task.FromResult(ApiKeyPermissions.Unverified);

        return QueryAsync(async token =>
        {
            var result = await Rest.SpotApi.Account.GetAPIKeyPermissionsAsync(ct: token).ConfigureAwait(false);
            return BinanceMappers.ToPermissions(Ensure(result, "api key permissions"));
        }, "api key permissions", ct);
    }

    public IDisposable SubscribeUserData(Func<UserDataEvent, CancellationToken, ValueTask> handler) =>
        _userData.Subscribe(Key, Market, handler);

    public StreamStatus UserDataStatus => _userData.GetStatus(KeyId, Market);

    protected Task<T> QueryAsync<T>(Func<CancellationToken, Task<T>> operation, string name, CancellationToken ct) =>
        _retry.ExecuteAsync(operation, RetryProfile.Query, name, ct);

    protected Task QueryAsync(Func<CancellationToken, Task> operation, string name, CancellationToken ct) =>
        _retry.ExecuteAsync(operation, RetryProfile.Query, name, ct);

    /// <summary>Orders are placed and cancelled exactly once; the caller decides about retries (FR-EXE-07).</summary>
    protected Task<T> OnceAsync<T>(Func<CancellationToken, Task<T>> operation, string name, CancellationToken ct) =>
        _retry.ExecuteAsync(operation, RetryProfile.None, name, ct);

    protected T Ensure<T>(HttpResult<T> result, string operation) =>
        BinanceErrorTranslator.EnsureSuccess(result, operation, Market, Now);

    protected void Ensure(HttpResult result, string operation) =>
        BinanceErrorTranslator.EnsureSuccess(result, operation, Market, Now);

    protected static string NormalizeSymbol(string symbol) => BinancePublicMarketData.NormalizeSymbol(symbol);

    protected static void RequireRef(OrderRef order)
    {
        if (order.IsEmpty)
            throw new ArgumentException("An order id or client order id is required.", nameof(order));
    }
}
