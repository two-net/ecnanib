using System.Globalization;
using System.Text.Json;
using Ecnanib.Kernel;
using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

internal sealed class BinanceAccount(BinanceTransport transport, BinanceServers servers, TimeProvider time) : IExchangeAccount
{
    /// <summary>Binance's code for an order it has no record of.</summary>
    private const int NoSuchOrder = -2013;

    /// <summary>The most a page of trades or income holds.</summary>
    private const int MaximumPage = 1000;

    public async Task<ExchangeAccount> GetAccountAsync(Market market, ExchangeKey key, RequestPriority priority, CancellationToken cancellationToken)
    {
        var server = ExchangeServers.Trading(market, key.Environment);

        if (market == Market.Spot)
        {
            var spot = await transport.SendAsync<SpotAccountBody>(
                server, BinanceEndpoint.SpotAccount, [("omitZeroBalances", "true")], key, priority, clientOrderId: null, cancellationToken);

            return new ExchangeAccount(
                market,
                spot.CanTrade,
                [.. spot.Balances.Select(balance => new AssetBalance(balance.Asset, balance.Free + balance.Locked, balance.Free))],
                TotalMarginBalance: null,
                AvailableBalance: null,
                spot.Uid);
        }

        var futures = await transport.SendAsync<FuturesAccountBody>(
            server, BinanceEndpoint.FuturesAccount, [], key, priority, clientOrderId: null, cancellationToken);

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
            ThrowIfUnsettled(server, key, order.ClientOrderId, exception);

            return null;
        }
    }

    /// <summary>
    /// Only Binance's own answer means an order or list doesn't exist, and only once a send of it whose outcome was
    /// unknown can no longer be accepted. Anything else says nothing about it.
    /// </summary>
    /// <exception cref="ExchangeException">It could still appear.</exception>
    private void ThrowIfUnsettled(ExchangeServer server, ExchangeKey key, string clientOrderId, ExchangeException exception)
    {
        var now = time.GetUtcNow();

        if (servers[server].UnsettledUntil(key.CredentialId, clientOrderId, now) is { } settlesAt)
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
    }

    public async Task<ExchangeOrderList?> GetOrderListAsync(
        ExchangeKey key,
        OrderListReference list,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        Check(list);

        var server = ExchangeServers.Trading(Market.Spot, key.Environment);

        try
        {
            var body = await transport.SendAsync<OrderListBody>(
                server, BinanceEndpoint.SpotQueryOrderList, [("origClientOrderId", list.ListClientOrderId)], key, priority, list.ListClientOrderId, cancellationToken);

            return BinanceOrders.FromList(body);
        }
        catch (ExchangeException exception) when (exception is { Failure: ExchangeFailure.Rejected, Code: NoSuchOrder })
        {
            // As for an order. Binance's code for a missing list isn't documented: -2013 is assumed, and anything else
            // throws, which is the safe direction. LiveBinanceTests checks it against Demo.
            ThrowIfUnsettled(server, key, list.ListClientOrderId, exception);

            return null;
        }
    }

    public async Task<IReadOnlyList<ExchangeOrder>> GetOpenOrdersAsync(
        Market market,
        ExchangeKey key,
        string symbol,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);

        var server = ExchangeServers.Trading(market, key.Environment);

        if (market == Market.Spot)
        {
            var spot = await transport.SendAsync<List<OrderBody>>(
                server, BinanceEndpoint.SpotOpenOrders, [("symbol", symbol)], key, priority, clientOrderId: null, cancellationToken);

            return [.. spot.Where(order => order.Symbol == symbol).Select(order => BinanceOrders.FromOrder(server, order))];
        }

        // Both end before either's failure is seen, so neither is left running.
        var orders = transport.SendAsync<List<OrderBody>>(
            server, BinanceEndpoint.FuturesOpenOrders, [("symbol", symbol)], key, priority, clientOrderId: null, cancellationToken);
        var algos = transport.SendAsync<List<AlgoOrderBody>>(
            server, BinanceEndpoint.FuturesOpenAlgoOrders, [("symbol", symbol)], key, priority, clientOrderId: null, cancellationToken);

        await ((Task)Task.WhenAll(orders, algos)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        return
        [
            .. (await orders).Where(order => order.Symbol == symbol).Select(order => BinanceOrders.FromOrder(server, order)),
            .. (await algos).Where(algo => algo.Symbol == symbol).Select(algo => BinanceOrders.FromAlgo(server, algo)),
        ];
    }

    public async Task<IReadOnlyList<ExchangeTrade>> GetOrderTradesAsync(
        Market market,
        ExchangeKey key,
        string symbol,
        long exchangeOrderId,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);
        ArgumentOutOfRangeException.ThrowIfLessThan(exchangeOrderId, 1);

        var trades = await transport.SendAsync<List<TradeBody>>(
            ExchangeServers.Trading(market, key.Environment),
            market == Market.Spot ? BinanceEndpoint.SpotMyTrades : BinanceEndpoint.FuturesUserTrades,
            [("symbol", symbol), ("orderId", exchangeOrderId.ToString(CultureInfo.InvariantCulture))],
            key,
            priority,
            clientOrderId: null,
            cancellationToken);

        return [.. trades.Where(trade => trade.OrderId == exchangeOrderId).Select(BinanceOrders.FromTrade).OrderBy(trade => trade.Time)];
    }

    public async Task<IReadOnlyList<ExchangeTrade>> GetTradesAsync(
        Market market,
        ExchangeKey key,
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        RequestPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);

        if (from > to)
        {
            throw new ArgumentException("The window starts after it ends.", nameof(from));
        }

        if (to - from > IExchangeAccount.MaximumTradeWindow(market))
        {
            throw new ArgumentException($"Binance allows a window of at most {IExchangeAccount.MaximumTradeWindow(market)} on {market}.", nameof(to));
        }

        var trades = await transport.SendAsync<List<TradeBody>>(
            ExchangeServers.Trading(market, key.Environment),
            market == Market.Spot ? BinanceEndpoint.SpotMyTradesByTime : BinanceEndpoint.FuturesUserTrades,
            [
                ("symbol", symbol),
                ("startTime", Milliseconds(from)),
                ("endTime", Milliseconds(to)),
                ("limit", MaximumPage.ToString(CultureInfo.InvariantCulture)),
            ],
            key,
            priority,
            clientOrderId: null,
            cancellationToken);

        return
        [
            .. trades
                .Where(trade => trade.Symbol == symbol)
                .Select(BinanceOrders.FromTrade)
                .Where(trade => trade.Time >= from && trade.Time <= to)
                .OrderBy(trade => trade.Time)
                .ThenBy(trade => trade.TradeId),
        ];
    }

    public async Task<IReadOnlyList<ExchangeFundingFee>> GetFundingFeesAsync(
        ExchangeKey key,
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        BinanceMarketData.CheckSymbol(symbol);

        if (from > to)
        {
            throw new ArgumentException("The window starts after it ends.", nameof(from));
        }

        var server = ExchangeServers.Trading(Market.Futures, key.Environment);
        var fees = new Dictionary<long, ExchangeFundingFee>();
        var start = from;

        // Binance answers a page of up to 1000, oldest first. The next page starts at the last one's time, which may
        // repeat it, so they're kept by transaction ID.
        while (true)
        {
            var page = await transport.SendAsync<List<IncomeBody>>(
                server,
                BinanceEndpoint.FuturesIncome,
                [
                    ("symbol", symbol),
                    ("incomeType", "FUNDING_FEE"),
                    ("startTime", Milliseconds(start)),
                    ("endTime", Milliseconds(to)),
                    ("limit", MaximumPage.ToString(CultureInfo.InvariantCulture)),
                ],
                key,
                RequestPriority.Normal,
                clientOrderId: null,
                cancellationToken);

            foreach (var income in page.Where(income => income.Symbol == symbol && income.IncomeType == "FUNDING_FEE"))
            {
                fees[income.TranId] = new ExchangeFundingFee(
                    income.Symbol, income.TranId, DateTimeOffset.FromUnixTimeMilliseconds(income.Time), income.Income, income.Asset);
            }

            if (page.Count < MaximumPage)
            {
                break;
            }

            var last = DateTimeOffset.FromUnixTimeMilliseconds(page.Max(income => income.Time));

            if (last <= start)
            {
                // A whole page at one millisecond would never move on.
                break;
            }

            start = last;
        }

        return [.. fees.Values.Where(fee => fee.Time >= from && fee.Time <= to).OrderBy(fee => fee.Time).ThenBy(fee => fee.TransactionId)];
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

    private static string Milliseconds(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    internal static void Check(OrderListReference list)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!OrderRequest.IsSymbol(list.Symbol) || !OrderRequest.IsClientOrderId(list.ListClientOrderId))
        {
            throw new ArgumentException("An order list reference needs a symbol and a client order ID that Binance accepts.", nameof(list));
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
