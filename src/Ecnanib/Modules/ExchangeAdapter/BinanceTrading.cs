using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

internal sealed class BinanceTrading(BinanceTransport transport, BinanceServers servers, TimeProvider time) : IExchangeTrading
{
    public async Task<ExchangeOrder> PlaceOrderAsync(Market market, ExchangeKey key, OrderRequest order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.ReduceOnly && market == Market.Spot)
        {
            throw new ArgumentException("Spot has no reduce-only orders.", nameof(order));
        }

        var server = ExchangeServers.Trading(market, key.Environment);

        if (BinanceOrders.IsAlgo(market, order.Type))
        {
            List<(string, string)> algo =
            [
                ("algoType", "CONDITIONAL"),
                ("symbol", order.Symbol),
                ("side", BinanceOrders.Side(order.Side)),
                ("type", BinanceOrders.Type(market, order.Type)),
                ("quantity", BinanceOrders.Number(order.Quantity)),
                ("triggerPrice", BinanceOrders.Number(order.StopPrice!.Value)),

                // The Risk Engine checks a stop against the mark price, so it triggers on it too, not on a last
                // trade that a thin book can spike.
                ("workingType", "MARK_PRICE"),
            ];

            if (order.ReduceOnly)
            {
                algo.Add(("reduceOnly", "true"));
            }

            algo.Add(("clientAlgoId", order.ClientOrderId));

            var placed = await transport.SendAsync<AlgoOrderBody>(
                server, BinanceEndpoint.FuturesNewAlgoOrder, algo, key, order.Priority, order.ClientOrderId, cancellationToken);

            return BinanceOrders.FromAlgo(server, placed);
        }

        List<(string, string)> parameters =
        [
            ("symbol", order.Symbol),
            ("side", BinanceOrders.Side(order.Side)),
            ("type", BinanceOrders.Type(market, order.Type)),
        ];

        if (order.TimeInForce is { } timeInForce)
        {
            parameters.Add(("timeInForce", BinanceOrders.TimeInForce(timeInForce)));
        }

        parameters.Add(("quantity", BinanceOrders.Number(order.Quantity)));

        if (order.Price is { } price)
        {
            parameters.Add(("price", BinanceOrders.Number(price)));
        }

        if (order.StopPrice is { } stopPrice)
        {
            parameters.Add(("stopPrice", BinanceOrders.Number(stopPrice)));
        }

        if (order.ReduceOnly)
        {
            parameters.Add(("reduceOnly", "true"));
        }

        parameters.Add(("newClientOrderId", order.ClientOrderId));
        parameters.Add(("newOrderRespType", "RESULT"));

        var endpoint = market == Market.Spot ? BinanceEndpoint.SpotNewOrder : BinanceEndpoint.FuturesNewOrder;
        var body = await transport.SendAsync<OrderBody>(server, endpoint, parameters, key, order.Priority, order.ClientOrderId, cancellationToken);

        return BinanceOrders.FromOrder(server, body);
    }

    public async Task<ExchangeOrderList> PlaceOrderListAsync(ExchangeKey key, OrderListRequest list, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(list);

        var server = ExchangeServers.Trading(Market.Spot, key.Environment);
        var sell = list.Side == OrderSide.Sell;

        // Closing a long sells above at the target and below at the stop. Closing a short buys the other way round.
        var (above, below) = sell
            ? ((Type: "LIMIT_MAKER", Id: list.TargetClientOrderId, Price: "abovePrice", Value: list.TargetPrice),
               (Type: "STOP_LOSS", Id: list.StopClientOrderId, Price: "belowStopPrice", Value: list.StopPrice))
            : ((Type: "STOP_LOSS", Id: list.StopClientOrderId, Price: "aboveStopPrice", Value: list.StopPrice),
               (Type: "LIMIT_MAKER", Id: list.TargetClientOrderId, Price: "belowPrice", Value: list.TargetPrice));

        List<(string, string)> parameters =
        [
            ("symbol", list.Symbol),
            ("listClientOrderId", list.ListClientOrderId),
            ("side", BinanceOrders.Side(list.Side)),
            ("quantity", BinanceOrders.Number(list.Quantity)),
            ("aboveType", above.Type),
            ("aboveClientOrderId", above.Id),
            (above.Price, BinanceOrders.Number(above.Value)),
            ("belowType", below.Type),
            ("belowClientOrderId", below.Id),
            (below.Price, BinanceOrders.Number(below.Value)),
            ("newOrderRespType", "RESULT"),
        ];

        try
        {
            var body = await transport.SendAsync<OrderListBody>(
                server, BinanceEndpoint.SpotNewOrderList, parameters, key, list.Priority, list.ListClientOrderId, cancellationToken);

            return BinanceOrders.FromList(body);
        }
        catch (ExchangeException exception) when (exception is { MayHaveExecuted: true, SettlesAt: { } settlesAt })
        {
            // The transport remembers the list. Its orders can be queried by their own IDs too, and must not be said
            // to be missing before the list could still be accepted.
            var state = servers[server];
            state.RecordUnsettled(key.CredentialId, list.TargetClientOrderId, settlesAt, time.GetUtcNow());
            state.RecordUnsettled(key.CredentialId, list.StopClientOrderId, settlesAt, time.GetUtcNow());

            throw;
        }
    }

    public async Task CancelOrderListAsync(ExchangeKey key, OrderListReference list, RequestPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceAccount.Check(list);

        await transport.SendAsync(
            ExchangeServers.Trading(Market.Spot, key.Environment),
            BinanceEndpoint.SpotCancelOrderList,
            [("symbol", list.Symbol), ("listClientOrderId", list.ListClientOrderId)],
            key,
            priority,
            list.ListClientOrderId,
            cancellationToken);
    }

    public async Task CancelOrderAsync(
        Market market,
        ExchangeKey key,
        OrderReference order,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        BinanceAccount.Check(order);

        var server = ExchangeServers.Trading(market, key.Environment);

        if (BinanceOrders.IsAlgo(market, order.Type))
        {
            await transport.SendAsync(
                server, BinanceEndpoint.FuturesCancelAlgoOrder, [("clientAlgoId", order.ClientOrderId)], key, priority, order.ClientOrderId, cancellationToken);

            return;
        }

        var endpoint = market == Market.Spot ? BinanceEndpoint.SpotCancelOrder : BinanceEndpoint.FuturesCancelOrder;
        await transport.SendAsync(
            server, endpoint, [("symbol", order.Symbol), ("origClientOrderId", order.ClientOrderId)], key, priority, order.ClientOrderId, cancellationToken);
    }

    public async Task SetMarginTypeAsync(ExchangeKey key, string symbol, FuturesMarginType marginType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);
        var type = BinanceOrders.MarginType(marginType);

        await transport.SendAsync(
            ExchangeServers.Trading(Market.Futures, key.Environment),
            BinanceEndpoint.FuturesMarginType,
            [("symbol", symbol), ("marginType", type)],
            key,
            RequestPriority.Normal,
            clientOrderId: null,
            cancellationToken);
    }

    public async Task<int> SetLeverageAsync(ExchangeKey key, string symbol, int leverage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);
        ArgumentOutOfRangeException.ThrowIfLessThan(leverage, 1);

        var body = await transport.SendAsync<LeverageBody>(
            ExchangeServers.Trading(Market.Futures, key.Environment),
            BinanceEndpoint.FuturesLeverage,
            [("symbol", symbol), ("leverage", leverage.ToString(CultureInfo.InvariantCulture))],
            key,
            RequestPriority.Normal,
            clientOrderId: null,
            cancellationToken);

        return body.Leverage;
    }
}
