using System.Globalization;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

internal sealed class BinanceTrading(BinanceTransport transport) : IExchangeTrading
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
