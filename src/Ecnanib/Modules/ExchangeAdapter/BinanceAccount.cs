using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

internal sealed class BinanceAccount(BinanceTransport transport, BinanceServers servers, TimeProvider time) : IExchangeAccount
{
    /// <summary>Binance's code for an order it has no record of.</summary>
    private const int NoSuchOrder = -2013;

    public async Task<ExchangeAccount> GetAccountAsync(Market market, ExchangeKey key, CancellationToken cancellationToken)
    {
        var server = ExchangeServers.Trading(market, key.Environment);

        if (market == Market.Spot)
        {
            var spot = await transport.SendAsync<SpotAccountBody>(
                server, BinanceEndpoint.SpotAccount, [("omitZeroBalances", "true")], key, RequestPriority.Normal, clientOrderId: null, cancellationToken);

            return new ExchangeAccount(
                market,
                spot.CanTrade,
                [.. spot.Balances.Select(balance => new AssetBalance(balance.Asset, balance.Free + balance.Locked, balance.Free))],
                TotalMarginBalance: null,
                AvailableBalance: null);
        }

        var futures = await transport.SendAsync<FuturesAccountBody>(
            server, BinanceEndpoint.FuturesAccount, [], key, RequestPriority.Normal, clientOrderId: null, cancellationToken);

        return new ExchangeAccount(
            market,
            CanTrade: null,
            [
                .. futures.Assets
                    .Where(asset => asset.WalletBalance != 0 || asset.AvailableBalance != 0)
                    .Select(asset => new AssetBalance(asset.Asset, asset.WalletBalance, asset.AvailableBalance)),
            ],
            futures.TotalMarginBalance,
            futures.AvailableBalance);
    }

    public async Task<ExchangeOrder?> GetOrderAsync(
        Market market,
        ExchangeKey key,
        OrderReference order,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        Check(order);

        var server = ExchangeServers.Trading(market, key.Environment);

        try
        {
            if (BinanceOrders.IsAlgo(market, order.Type))
            {
                var algo = await transport.SendAsync<AlgoOrderBody>(
                    server, BinanceEndpoint.FuturesQueryAlgoOrder, [("clientAlgoId", order.ClientOrderId)], key, priority, order.ClientOrderId, cancellationToken);

                return BinanceOrders.FromAlgo(server, algo);
            }

            var endpoint = market == Market.Spot ? BinanceEndpoint.SpotQueryOrder : BinanceEndpoint.FuturesQueryOrder;
            var body = await transport.SendAsync<OrderBody>(
                server, endpoint, [("symbol", order.Symbol), ("origClientOrderId", order.ClientOrderId)], key, priority, order.ClientOrderId, cancellationToken);

            return BinanceOrders.FromOrder(server, body);
        }
        catch (ExchangeException exception) when (exception is { Failure: ExchangeFailure.Rejected, Code: NoSuchOrder })
        {
            // Only Binance's own answer means the order doesn't exist, and only once a send of it whose
            // outcome was unknown can no longer be accepted. Anything else says nothing about it.
            var now = time.GetUtcNow();

            if (servers[server].UnsettledUntil(key.CredentialId, order.ClientOrderId, now) is { } settlesAt)
            {
                throw new ExchangeException(
                    server,
                    ExchangeFailure.Unknown,
                    $"{ExchangeServers.Describe(server)} has no such order yet, but one sent with an unknown outcome could still be accepted.",
                    NoSuchOrder,
                    settlesAt - now,
                    exception,
                    settlesAt);
            }

            return null;
        }
    }

    internal static void Check(OrderReference order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!OrderRequest.IsSymbol(order.Symbol) || !OrderRequest.IsClientOrderId(order.ClientOrderId))
        {
            throw new ArgumentException("An order reference needs a symbol and a client order ID that Binance accepts.", nameof(order));
        }
    }
}
