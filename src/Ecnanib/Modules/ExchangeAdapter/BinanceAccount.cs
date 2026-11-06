using System.Text.Json;
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
                AvailableBalance: null,
                spot.Uid);
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

    public async Task<ApiRestrictions> GetApiRestrictionsAsync(ExchangeKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Environment != TradingEnvironment.Live)
        {
            throw new ArgumentException("Binance documents a key's restrictions for Live keys only.", nameof(key));
        }

        var body = await transport.SendAsync<ApiRestrictionsBody>(
            ExchangeServer.SpotLive, BinanceEndpoint.SpotApiRestrictions, [], key, RequestPriority.Normal, clientOrderId: null, cancellationToken);

        return new ApiRestrictions(
            body.IpRestrict, body.EnableReading, body.EnableWithdrawals, body.EnableSpotAndMarginTrading, body.EnableFutures);
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

    public async Task<IReadOnlyList<FuturesPosition>> GetPositionAsync(
        ExchangeKey key,
        string symbol,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);

        var positions = await transport.SendAsync<List<PositionRiskBody>>(
            ExchangeServers.Trading(Market.Futures, key.Environment),
            BinanceEndpoint.FuturesPositionRisk,
            [("symbol", symbol)],
            key,
            priority,
            clientOrderId: null,
            cancellationToken);

        // Binance also lists a symbol that has only open orders, with an amount of 0.
        return
        [
            .. positions
                .Where(position => position.Symbol == symbol && position.PositionAmt != 0)
                .Select(position => new FuturesPosition(
                    position.Symbol,
                    position.PositionSide,
                    position.PositionAmt,
                    position.EntryPrice,
                    position.BreakEvenPrice,
                    position.MarkPrice,
                    position.LiquidationPrice > 0 ? position.LiquidationPrice : null,
                    position.UnRealizedProfit,
                    position.MarginAsset,
                    position.UpdateTime is > 0 and var updated ? DateTimeOffset.FromUnixTimeMilliseconds(updated) : null)),
        ];
    }

    public async Task<FuturesAccountConfig> GetFuturesAccountConfigAsync(ExchangeKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var body = await transport.SendAsync<FuturesAccountConfigBody>(
            ExchangeServers.Trading(Market.Futures, key.Environment),
            BinanceEndpoint.FuturesAccountConfig,
            [],
            key,
            RequestPriority.Normal,
            clientOrderId: null,
            cancellationToken);

        return new FuturesAccountConfig(body.CanTrade, body.DualSidePosition, body.MultiAssetsMargin);
    }

    public async Task<FuturesSymbolConfig?> GetFuturesSymbolConfigAsync(ExchangeKey key, string symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);

        var configs = await transport.SendAsync<List<FuturesSymbolConfigBody>>(
            ExchangeServers.Trading(Market.Futures, key.Environment),
            BinanceEndpoint.FuturesSymbolConfig,
            [("symbol", symbol)],
            key,
            RequestPriority.Normal,
            clientOrderId: null,
            cancellationToken);

        return configs.FirstOrDefault(config => config.Symbol == symbol) is { } found
            ? new FuturesSymbolConfig(found.Symbol, BinanceOrders.MarginType(found.MarginType), found.Leverage)
            : null;
    }

    public async Task<IReadOnlyList<ExchangeLeverageBracket>> GetLeverageBracketsAsync(
        ExchangeKey key, string symbol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);

        var server = ExchangeServers.Trading(Market.Futures, key.Environment);
        var endpoint = BinanceEndpoint.FuturesLeverageBracket;
        var body = await transport.SendAsync(server, endpoint, [("symbol", symbol)], key, RequestPriority.Normal, clientOrderId: null, cancellationToken);

        try
        {
            return ReadBrackets(body, symbol);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw BinanceMarketData.Unreadable(server, endpoint, exception);
        }
    }

    /// <summary>
    /// Binance documents the answer as either an array of symbols or one symbol on its own, without saying which
    /// comes when one symbol is asked for, so both are read.
    /// </summary>
    private static List<ExchangeLeverageBracket> ReadBrackets(byte[] body, string symbol)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var entry = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray()
                .Select(element => element.Deserialize<LeverageBracketsBody>(BinanceJson.Options))
                .FirstOrDefault(found => found?.Symbol == symbol),
            JsonValueKind.Object => root.Deserialize<LeverageBracketsBody>(BinanceJson.Options) is { } single && single.Symbol == symbol ? single : null,
            _ => throw new FormatException("Leverage brackets are neither an array nor an object."),
        };

        return entry is null
            ? []
            :
            [
                .. entry.Brackets
                    .OrderBy(bracket => bracket.NotionalFloor)
                    .Select(bracket => new ExchangeLeverageBracket(
                        bracket.Bracket,
                        bracket.InitialLeverage,
                        bracket.NotionalFloor,
                        bracket.NotionalCap,
                        bracket.MaintMarginRatio,
                        bracket.Cum)),
            ];
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
