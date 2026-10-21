using Binance.Net.Enums;
using BinanceTimeInForce = Binance.Net.Enums.TimeInForce;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Resilience;
using Ecnanib.Exchange.Streaming;
using PositionMode = Ecnanib.Exchange.Abstractions.PositionMode;
using MarginType = Ecnanib.Exchange.Abstractions.MarginType;

namespace Ecnanib.Exchange.Binance;

/// <summary>Binance USD-M futures account, positions, position settings and orders for one key (EX-02, FR-EXE-02, FR-EXE-11).</summary>
internal sealed class BinanceFuturesAccountClient : BinanceAccountClientBase, IFuturesAccountClient
{
    public BinanceFuturesAccountClient(ExchangeKeyHandle key, IBinanceClientRegistry registry, ExchangeRetryPolicy retry, IUserDataStreams userData, TimeProvider time)
        : base(key, MarketType.UsdFutures, registry, retry, userData, time)
    {
    }

    public override Task PingAsync(CancellationToken ct) => QueryAsync(async token =>
    {
        Ensure(await Rest.UsdFuturesApi.Account.GetAccountInfoV3Async(ct: token).ConfigureAwait(false), "ping");
    }, "ping", ct);

    public override Task<IReadOnlyList<AssetBalance>> GetBalancesAsync(CancellationToken ct) => QueryAsync<IReadOnlyList<AssetBalance>>(async token =>
    {
        var balances = Ensure(await Rest.UsdFuturesApi.Account.GetBalancesAsync(ct: token).ConfigureAwait(false), "balances");
        return balances.Select(b => BinanceMappers.ToBalance(b)).ToArray();
    }, "balances", ct);

    public Task<FuturesAccountSummary> GetAccountSummaryAsync(CancellationToken ct) => QueryAsync(async token =>
    {
        var info = Ensure(await Rest.UsdFuturesApi.Account.GetAccountInfoV3Async(ct: token).ConfigureAwait(false), "account summary");
        return BinanceMappers.ToAccountSummary(info, Now);
    }, "account summary", ct);

    public Task<IReadOnlyList<FuturesPosition>> GetPositionsAsync(string? symbol, CancellationToken ct)
    {
        var normalized = symbol is null ? null : NormalizeSymbol(symbol);
        return QueryAsync<IReadOnlyList<FuturesPosition>>(async token =>
        {
            var positions = Ensure(await Rest.UsdFuturesApi.Trading.GetPositionsAsync(normalized, ct: token).ConfigureAwait(false), "positions");
            var now = Now;
            return positions.Select(p => BinanceMappers.ToPosition(p, now)).ToArray();
        }, "positions", ct);
    }

    public Task<PositionMode> GetPositionModeAsync(CancellationToken ct) => QueryAsync(async token =>
    {
        var mode = Ensure(await Rest.UsdFuturesApi.Account.GetPositionModeAsync(ct: token).ConfigureAwait(false), "position mode");
        return mode.IsHedgeMode ? PositionMode.Hedge : PositionMode.OneWay;
    }, "position mode", ct);

    public Task SetPositionModeAsync(PositionMode mode, CancellationToken ct) => QueryAsync(async token =>
    {
        var result = await Rest.UsdFuturesApi.Account.ModifyPositionModeAsync(dualPositionSide: mode == PositionMode.Hedge, ct: token).ConfigureAwait(false);
        if (!result.Success && BinanceErrorTranslator.IsNoChangeNeeded(result.Error))
            return;
        Ensure(result, "set position mode");
    }, "set position mode", ct);

    public Task<int> SetLeverageAsync(string symbol, int leverage, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        ArgumentOutOfRangeException.ThrowIfLessThan(leverage, 1);
        return QueryAsync(async token =>
        {
            var result = Ensure(await Rest.UsdFuturesApi.Account.ChangeInitialLeverageAsync(normalized, leverage, ct: token).ConfigureAwait(false), "set leverage");
            return result.Leverage;
        }, "set leverage", ct);
    }

    public Task SetMarginTypeAsync(string symbol, MarginType marginType, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        return QueryAsync(async token =>
        {
            var result = await Rest.UsdFuturesApi.Account.ChangeMarginTypeAsync(normalized, BinanceMappers.FromMarginType(marginType), ct: token).ConfigureAwait(false);
            if (!result.Success && BinanceErrorTranslator.IsNoChangeNeeded(result.Error))
                return;
            Ensure(result, "set margin type");
        }, "set margin type", ct);
    }

    /// <summary>
    /// Always one-way mode (A-06): <c>positionSide=BOTH</c>. Stop and take-profit types are refused here before anything
    /// is sent: Binance answers -4120 for them and they go through <see cref="PlaceConditionalOrderAsync"/>.
    /// </summary>
    public override Task<ExchangeOrder> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsConditional(request.Type))
            throw new ArgumentException($"{request.Type} orders are conditional orders on USD-M futures; use PlaceConditionalOrderAsync (Binance -4120).", nameof(request));
        var type = BinanceMappers.ToFuturesOrderType(request.Type);
        var tif = DefaultTimeInForce(request);
        return OnceAsync(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.PlaceOrderAsync(
                request.Symbol,
                BinanceMappers.FromOrderSide(request.Side),
                type,
                quantity: request.ClosePosition ? null : request.Quantity,
                price: request.Price,
                positionSide: PositionSide.Both,
                timeInForce: tif,
                reduceOnly: request.ReduceOnly && !request.ClosePosition ? true : null,
                newClientOrderId: request.ClientOrderId,
                stopPrice: request.StopPrice,
                workingType: request.WorkingType is { } w ? BinanceMappers.FromWorkingType(w) : null,
                closePosition: request.ClosePosition ? true : null,
                orderResponseType: OrderResponseType.Result,
                ct: token).ConfigureAwait(false);
            return BinanceMappers.ToOrder(Ensure(result, "place order"));
        }, "place order", ct);
    }

    public override Task<ExchangeOrder> GetOrderAsync(string symbol, OrderRef order, CancellationToken ct)
    {
        RequireRef(order);
        var normalized = NormalizeSymbol(symbol);
        return QueryAsync(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.GetOrderAsync(normalized, orderId: order.OrderId, origClientOrderId: order.ClientOrderId, ct: token).ConfigureAwait(false);
            return BinanceMappers.ToOrder(Ensure(result, "get order"));
        }, "get order", ct);
    }

    public override Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct)
    {
        var normalized = symbol is null ? null : NormalizeSymbol(symbol);
        return QueryAsync<IReadOnlyList<ExchangeOrder>>(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.GetOpenOrdersAsync(normalized, ct: token).ConfigureAwait(false);
            return Ensure(result, "open orders").Select(o => BinanceMappers.ToOrder(o)).ToArray();
        }, "open orders", ct);
    }

    public override Task<ExchangeOrder> CancelOrderAsync(string symbol, OrderRef order, CancellationToken ct)
    {
        RequireRef(order);
        var normalized = NormalizeSymbol(symbol);
        return OnceAsync(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.CancelOrderAsync(normalized, orderId: order.OrderId, origClientOrderId: order.ClientOrderId, ct: token).ConfigureAwait(false);
            return BinanceMappers.ToOrder(Ensure(result, "cancel order"));
        }, "cancel order", ct);
    }

    /// <summary>Binance's futures cancel-all returns only a status, so the open orders are read first and returned.</summary>
    public override async Task<IReadOnlyList<ExchangeOrder>> CancelAllOrdersAsync(string symbol, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        var open = await GetOpenOrdersAsync(normalized, ct).ConfigureAwait(false);
        await OnceAsync(async token =>
        {
            Ensure(await Rest.UsdFuturesApi.Trading.CancelAllOrdersAsync(normalized, ct: token).ConfigureAwait(false), "cancel all orders");
            return true;
        }, "cancel all orders", ct).ConfigureAwait(false);
        return open;
    }

    public override Task<IReadOnlyList<UserTrade>> GetTradesAsync(string symbol, DateTimeOffset? from, long? fromTradeId, int? limit, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        return QueryAsync<IReadOnlyList<UserTrade>>(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.GetUserTradesAsync(normalized, limit: limit, fromId: fromTradeId, startTime: from?.UtcDateTime, ct: token).ConfigureAwait(false);
            return Ensure(result, "trades").Select(BinanceMappers.ToTrade).ToArray();
        }, "trades", ct);
    }

    public Task<ConditionalOrder> PlaceConditionalOrderAsync(ConditionalOrderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = BinanceMappers.ToConditionalOrderType(request.Type);
        var tif = request.Type is OrderType.StopLossLimit or OrderType.TakeProfitLimit ? BinanceTimeInForce.GoodTillCanceled : (BinanceTimeInForce?)null;
        return OnceAsync(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.PlaceConditionalOrderAsync(
                request.Symbol,
                BinanceMappers.FromOrderSide(request.Side),
                type,
                quantity: request.Quantity,
                price: request.Price,
                positionSide: PositionSide.Both,
                timeInForce: tif,
                reduceOnly: request.ReduceOnly ? true : null,
                clientOrderId: request.ClientAlgoId,
                triggerPrice: request.TriggerPrice,
                workingType: request.WorkingType is { } w ? BinanceMappers.FromWorkingType(w) : null,
                closePosition: request.ClosePosition ? true : null,
                priceProtect: request.PriceProtect ? true : null,
                ct: token).ConfigureAwait(false);
            return BinanceMappers.ToConditionalOrder(Ensure(result, "place conditional order"));
        }, "place conditional order", ct);
    }

    public Task<ConditionalOrder> GetConditionalOrderAsync(OrderRef order, CancellationToken ct)
    {
        RequireRef(order);
        return QueryAsync(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.GetConditionalOrderAsync(orderId: order.OrderId, clientOrderId: order.ClientOrderId, ct: token).ConfigureAwait(false);
            return BinanceMappers.ToConditionalOrder(Ensure(result, "get conditional order"));
        }, "get conditional order", ct);
    }

    public Task<ConditionalOrder> CancelConditionalOrderAsync(OrderRef order, CancellationToken ct)
    {
        RequireRef(order);
        return OnceAsync(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.CancelConditionalOrderAsync(orderId: order.OrderId, clientOrderId: order.ClientOrderId, ct: token).ConfigureAwait(false);
            return BinanceMappers.ToConditionalOrder(Ensure(result, "cancel conditional order"));
        }, "cancel conditional order", ct);
    }

    public Task<IReadOnlyList<ConditionalOrder>> GetOpenConditionalOrdersAsync(string? symbol, CancellationToken ct)
    {
        var normalized = symbol is null ? null : NormalizeSymbol(symbol);
        return QueryAsync<IReadOnlyList<ConditionalOrder>>(async token =>
        {
            var result = await Rest.UsdFuturesApi.Trading.GetOpenConditionalOrdersAsync(symbol: normalized, ct: token).ConfigureAwait(false);
            return Ensure(result, "open conditional orders").Select(BinanceMappers.ToConditionalOrder).ToArray();
        }, "open conditional orders", ct);
    }

    public Task<IReadOnlyList<FundingPayment>> GetFundingPaymentsAsync(string symbol, DateTimeOffset from, DateTimeOffset? to, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        return QueryAsync<IReadOnlyList<FundingPayment>>(async token =>
        {
            var result = await Rest.UsdFuturesApi.Account.GetIncomeHistoryAsync(normalized, "FUNDING_FEE", startTime: from.UtcDateTime, endTime: to?.UtcDateTime, limit: 1000, ct: token).ConfigureAwait(false);
            return Ensure(result, "funding payments").Select(BinanceMappers.ToFundingPayment).OrderBy(p => p.Timestamp).ToArray();
        }, "funding payments", ct);
    }

    private static bool IsConditional(OrderType type) =>
        type is OrderType.StopMarket or OrderType.TakeProfitMarket or OrderType.StopLossLimit or OrderType.TakeProfitLimit;

    private static BinanceTimeInForce? DefaultTimeInForce(OrderRequest request)
    {
        if (request.TimeInForce is { } tif)
            return BinanceMappers.FromTimeInForce(tif);
        return request.Type is OrderType.Limit or OrderType.StopLossLimit or OrderType.TakeProfitLimit
            ? BinanceTimeInForce.GoodTillCanceled
            : null;
    }
}
