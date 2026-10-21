using CryptoExchange.Net.Objects.Sockets;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Resilience;
using Ecnanib.Exchange.Streaming;

namespace Ecnanib.Exchange.Binance;

/// <summary>
/// Keyed Binance user-data subscriptions and REST snapshots. Binance.Net requests and keeps alive the futures listen
/// key itself; the spot stream runs over the WebSocket API. Expiry of either surfaces as <c>onTerminated</c>.
/// </summary>
internal sealed class BinanceUserDataSource : IUserDataSource
{
    private readonly IBinanceClientRegistry _registry;
    private readonly ExchangeRetryPolicy _retry;
    private readonly TimeProvider _time;
    private readonly ILogger<BinanceUserDataSource> _logger;

    public BinanceUserDataSource(IBinanceClientRegistry registry, ExchangeRetryPolicy retry, TimeProvider time, ILogger<BinanceUserDataSource> logger)
    {
        _registry = registry;
        _retry = retry;
        _time = time;
        _logger = logger;
    }

    public async Task<StreamSubscriptionHandle> SubscribeAsync(ExchangeKeyHandle key, MarketType market, Action<UserDataEvent> onEvent, Action onTerminated, CancellationToken ct)
    {
        var socket = _registry.GetKeyedSocket(key);
        var keyId = key.KeyId;
        CryptoExchange.Net.Objects.WebSocketResult<UpdateSubscription> result;
        if (market == MarketType.Spot)
        {
            result = await socket.SpotApi.Account.SubscribeToUserDataUpdatesAsync(
                onOrderUpdateMessage: e => Safe(() =>
                {
                    var (order, execution, fill) = BinanceMappers.ToOrderUpdate(e.Data);
                    onEvent(new UserDataEvent.OrderUpdate(keyId, market, order.UpdatedAt, order, execution, fill));
                }, "spot order update"),
                onOcoOrderUpdateMessage: e => Safe(() =>
                {
                    var list = BinanceMappers.ToOcoList(e.Data);
                    onEvent(new UserDataEvent.OcoUpdate(keyId, market, list.TransactionTime, list));
                }, "spot oco update"),
                onAccountPositionMessage: e => Safe(() =>
                {
                    onEvent(new UserDataEvent.BalanceUpdate(keyId, market, BinanceMappers.ToUtc(e.Data.Timestamp), e.Data.Balances.Select(BinanceMappers.ToBalance).ToArray()));
                }, "spot balance update"),
                onAccountBalanceUpdate: null,
                onUserDataStreamTerminated: _ => Safe(onTerminated, "spot stream terminated"),
                onBalanceLockUpdate: null,
                ct: ct).ConfigureAwait(false);
        }
        else
        {
            result = await socket.UsdFuturesApi.Account.SubscribeToUserDataUpdatesAsync(
                onLeverageUpdate: e => Safe(() =>
                {
                    if (e.Data.LeverageUpdateData is { } leverage)
                        onEvent(new UserDataEvent.LeverageUpdate(keyId, market, BinanceMappers.ToUtc(e.Data.TransactionTime), leverage.Symbol ?? string.Empty, Convert.ToInt32(leverage.Leverage)));
                }, "futures leverage update"),
                onMarginUpdate: null,
                onAccountUpdate: e => Safe(() =>
                {
                    var data = e.Data.UpdateData;
                    var timestamp = BinanceMappers.ToUtc(e.Data.TransactionTime);
                    onEvent(new UserDataEvent.BalanceUpdate(keyId, market, timestamp, data.Balances.Select(BinanceMappers.ToBalance).ToArray()));
                    if (data.Positions.Length > 0)
                        onEvent(new UserDataEvent.PositionUpdate(keyId, market, timestamp, data.Positions.Select(p => BinanceMappers.ToPosition(p, timestamp)).ToArray(), data.Reason.ToString()));
                }, "futures account update"),
                onOrderUpdate: e => Safe(() =>
                {
                    var (order, execution, fill) = BinanceMappers.ToOrderUpdate(e.Data);
                    onEvent(new UserDataEvent.OrderUpdate(keyId, market, order.UpdatedAt, order, execution, fill));
                }, "futures order update"),
                onTradeUpdate: null,
                onListenKeyExpired: _ => Safe(onTerminated, "futures listen key expired"),
                onStrategyUpdate: null,
                onGridUpdate: null,
                onConditionalOrderTriggerRejectUpdate: null,
                onAlgoOrderUpdate: e => Safe(() =>
                {
                    var order = BinanceMappers.ToConditionalOrder(e.Data);
                    onEvent(new UserDataEvent.ConditionalOrderUpdate(keyId, market, order.UpdatedAt, order));
                }, "futures algo order update"),
                ct: ct).ConfigureAwait(false);
        }

        return new BinanceSubscriptionHandle(BinanceErrorTranslator.EnsureSuccess(result, "subscribe user data", market));
    }

    public Task<UserDataEvent.Snapshot> SnapshotAsync(ExchangeKeyHandle key, MarketType market, CancellationToken ct) => _retry.ExecuteAsync(async token =>
    {
        var rest = _registry.GetKeyedRest(key);
        var now = _time.GetUtcNow();
        if (market == MarketType.Spot)
        {
            var orders = BinanceErrorTranslator.EnsureSuccess(await rest.SpotApi.Trading.GetOpenOrdersAsync(ct: token).ConfigureAwait(false), "open orders", market, now);
            var account = BinanceErrorTranslator.EnsureSuccess(await rest.SpotApi.Account.GetAccountInfoAsync(omitZeroBalances: true, ct: token).ConfigureAwait(false), "balances", market, now);
            return new UserDataEvent.Snapshot(key.KeyId, market, _time.GetUtcNow(),
                orders.Select(o => BinanceMappers.ToOrder(o)).ToArray(), null, account.Balances.Select(BinanceMappers.ToBalance).ToArray());
        }

        var openOrders = BinanceErrorTranslator.EnsureSuccess(await rest.UsdFuturesApi.Trading.GetOpenOrdersAsync(ct: token).ConfigureAwait(false), "open orders", market, now);
        var positions = BinanceErrorTranslator.EnsureSuccess(await rest.UsdFuturesApi.Trading.GetPositionsAsync(ct: token).ConfigureAwait(false), "positions", market, now);
        var balances = BinanceErrorTranslator.EnsureSuccess(await rest.UsdFuturesApi.Account.GetBalancesAsync(ct: token).ConfigureAwait(false), "balances", market, now);
        var conditional = BinanceErrorTranslator.EnsureSuccess(await rest.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(ct: token).ConfigureAwait(false), "open conditional orders", market, now);
        var timestamp = _time.GetUtcNow();
        return new UserDataEvent.Snapshot(key.KeyId, market, timestamp,
            openOrders.Select(o => BinanceMappers.ToOrder(o)).ToArray(),
            positions.Select(p => BinanceMappers.ToPosition(p, timestamp)).ToArray(),
            balances.Select(b => BinanceMappers.ToBalance(b)).ToArray(),
            conditional.Select(BinanceMappers.ToConditionalOrder).ToArray());
    }, RetryProfile.Query, "user data snapshot", ct);

    private void Safe(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle a {What} message", what);
        }
    }
}
