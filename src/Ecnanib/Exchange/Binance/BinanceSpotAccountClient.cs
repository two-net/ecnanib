using Binance.Net.Enums;
using BinanceTimeInForce = Binance.Net.Enums.TimeInForce;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Resilience;
using Ecnanib.Exchange.Streaming;
using OrderSide = Ecnanib.Exchange.Abstractions.OrderSide;
using TimeInForce = Ecnanib.Exchange.Abstractions.TimeInForce;

namespace Ecnanib.Exchange.Binance;

/// <summary>Binance Spot account, orders and OCO exits for one key (EX-01, FR-EXE-01).</summary>
internal sealed class BinanceSpotAccountClient : BinanceAccountClientBase, ISpotAccountClient
{
    public BinanceSpotAccountClient(ExchangeKeyHandle key, IBinanceClientRegistry registry, ExchangeRetryPolicy retry, IUserDataStreams userData, TimeProvider time)
        : base(key, MarketType.Spot, registry, retry, userData, time)
    {
    }

    public override Task PingAsync(CancellationToken ct) => QueryAsync(async token =>
    {
        Ensure(await Rest.SpotApi.Account.GetAccountInfoAsync(omitZeroBalances: true, ct: token).ConfigureAwait(false), "ping");
    }, "ping", ct);

    public override Task<IReadOnlyList<AssetBalance>> GetBalancesAsync(CancellationToken ct) => QueryAsync<IReadOnlyList<AssetBalance>>(async token =>
    {
        var info = Ensure(await Rest.SpotApi.Account.GetAccountInfoAsync(omitZeroBalances: true, ct: token).ConfigureAwait(false), "balances");
        return info.Balances.Select(BinanceMappers.ToBalance).ToArray();
    }, "balances", ct);

    public override Task<ExchangeOrder> PlaceOrderAsync(OrderRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReduceOnly || request.ClosePosition || request.WorkingType is not null)
            throw new ArgumentException("ReduceOnly, ClosePosition and WorkingType apply to futures orders only.", nameof(request));

        var type = BinanceMappers.ToSpotOrderType(request.Type);
        var tif = DefaultTimeInForce(request);
        return OnceAsync(async token =>
        {
            var result = await Rest.SpotApi.Trading.PlaceOrderAsync(
                request.Symbol,
                BinanceMappers.FromOrderSide(request.Side),
                type,
                quantity: request.Quantity,
                price: request.Price,
                newClientOrderId: request.ClientOrderId,
                timeInForce: tif,
                stopPrice: request.StopPrice,
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
            var result = await Rest.SpotApi.Trading.GetOrderAsync(normalized, orderId: order.OrderId, origClientOrderId: order.ClientOrderId, ct: token).ConfigureAwait(false);
            return BinanceMappers.ToOrder(Ensure(result, "get order"));
        }, "get order", ct);
    }

    public override Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(string? symbol, CancellationToken ct)
    {
        var normalized = symbol is null ? null : NormalizeSymbol(symbol);
        return QueryAsync<IReadOnlyList<ExchangeOrder>>(async token =>
        {
            var result = await Rest.SpotApi.Trading.GetOpenOrdersAsync(normalized, ct: token).ConfigureAwait(false);
            return Ensure(result, "open orders").Select(o => BinanceMappers.ToOrder(o)).ToArray();
        }, "open orders", ct);
    }

    public override Task<ExchangeOrder> CancelOrderAsync(string symbol, OrderRef order, CancellationToken ct)
    {
        RequireRef(order);
        var normalized = NormalizeSymbol(symbol);
        return OnceAsync(async token =>
        {
            var result = await Rest.SpotApi.Trading.CancelOrderAsync(normalized, orderId: order.OrderId, origClientOrderId: order.ClientOrderId, ct: token).ConfigureAwait(false);
            return BinanceMappers.ToOrder(Ensure(result, "cancel order"));
        }, "cancel order", ct);
    }

    public override Task<IReadOnlyList<ExchangeOrder>> CancelAllOrdersAsync(string symbol, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        return OnceAsync<IReadOnlyList<ExchangeOrder>>(async token =>
        {
            var result = await Rest.SpotApi.Trading.CancelAllOrdersAsync(normalized, ct: token).ConfigureAwait(false);
            return Ensure(result, "cancel all orders").Select(o => BinanceMappers.ToOrder(o)).ToArray();
        }, "cancel all orders", ct);
    }

    public override Task<IReadOnlyList<UserTrade>> GetTradesAsync(string symbol, DateTimeOffset? from, long? fromTradeId, int? limit, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        return QueryAsync<IReadOnlyList<UserTrade>>(async token =>
        {
            var result = await Rest.SpotApi.Trading.GetUserTradesAsync(normalized, limit: limit, fromId: fromTradeId, startTime: from?.UtcDateTime, ct: token).ConfigureAwait(false);
            return Ensure(result, "trades").Select(BinanceMappers.ToTrade).ToArray();
        }, "trades", ct);
    }

    /// <summary>
    /// Places the OCO list. Binance names the legs by price position relative to the market: for a Sell exit the
    /// take-profit (limit maker) is the "above" leg and the stop-loss-limit the "below" leg; for a Buy they swap.
    /// </summary>
    public Task<OcoOrderList> PlaceOcoAsync(SpotOcoRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return OnceAsync(async token =>
        {
            var side = BinanceMappers.FromOrderSide(request.Side);
            var result = request.Side == OrderSide.Sell
                ? await Rest.SpotApi.Trading.PlaceOcoOrderListAsync(
                    request.Symbol, side, request.Quantity,
                    aboveOrderType: SpotOrderType.LimitMaker,
                    belowOrderType: SpotOrderType.StopLossLimit,
                    listClientOrderId: request.ListClientOrderId,
                    aboveClientOrderId: request.TakeProfitClientOrderId,
                    abovePrice: request.TakeProfitPrice,
                    belowClientOrderId: request.StopClientOrderId,
                    belowPrice: request.StopLimitPrice,
                    belowStopPrice: request.StopPrice,
                    belowTimeInForce: BinanceTimeInForce.GoodTillCanceled,
                    ct: token).ConfigureAwait(false)
                : await Rest.SpotApi.Trading.PlaceOcoOrderListAsync(
                    request.Symbol, side, request.Quantity,
                    aboveOrderType: SpotOrderType.StopLossLimit,
                    belowOrderType: SpotOrderType.LimitMaker,
                    listClientOrderId: request.ListClientOrderId,
                    aboveClientOrderId: request.StopClientOrderId,
                    abovePrice: request.StopLimitPrice,
                    aboveStopPrice: request.StopPrice,
                    aboveTimeInForce: BinanceTimeInForce.GoodTillCanceled,
                    belowClientOrderId: request.TakeProfitClientOrderId,
                    belowPrice: request.TakeProfitPrice,
                    ct: token).ConfigureAwait(false);
            return BinanceMappers.ToOcoList(Ensure(result, "place oco"));
        }, "place oco", ct);
    }

    public Task<OcoOrderList> CancelOcoAsync(string symbol, string listClientOrderId, CancellationToken ct)
    {
        var normalized = NormalizeSymbol(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(listClientOrderId);
        return OnceAsync(async token =>
        {
            var result = await Rest.SpotApi.Trading.CancelOcoOrderAsync(normalized, listClientOrderId: listClientOrderId, ct: token).ConfigureAwait(false);
            return BinanceMappers.ToOcoList(Ensure(result, "cancel oco"));
        }, "cancel oco", ct);
    }

    public Task<OcoOrderList> GetOcoAsync(string listClientOrderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listClientOrderId);
        return QueryAsync(async token =>
        {
            var result = await Rest.SpotApi.Trading.GetOcoOrderAsync(listClientOrderId: listClientOrderId, ct: token).ConfigureAwait(false);
            return BinanceMappers.ToOcoList(Ensure(result, "get oco"));
        }, "get oco", ct);
    }

    private static BinanceTimeInForce? DefaultTimeInForce(OrderRequest request)
    {
        if (request.TimeInForce is { } tif)
            return BinanceMappers.FromTimeInForce(tif);
        return request.Type is OrderType.Limit or OrderType.StopLossLimit or OrderType.TakeProfitLimit
            ? BinanceTimeInForce.GoodTillCanceled
            : null;
    }
}
