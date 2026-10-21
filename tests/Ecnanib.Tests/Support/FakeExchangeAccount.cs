using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Tests.Support;

/// <summary>
/// The scripted book of one key and market behind <see cref="FakeExchangeClientFactory"/>: balances, the account
/// summary, position settings, regular orders, spot OCO lists, futures conditional orders, fills and funding. Market
/// orders fill at once at <see cref="MarketPrice"/> (with <see cref="FeeRate"/> in the quote asset) unless
/// <see cref="FillMarketOrders"/> is off; every other order waits for the test to <see cref="FillAsync"/> or
/// <see cref="TriggerAsync"/> it, which also pushes the matching user-data event to the subscribers. Hooks script
/// failures; every request is recorded.
/// </summary>
internal sealed class FakeExchangeAccount
{
    private readonly object _lock = new();
    private readonly List<Func<UserDataEvent, CancellationToken, ValueTask>> _subscribers = [];
    private long _nextId = 1000;
    private long _nextTradeId = 5000;

    public FakeExchangeAccount(string keyId, MarketType market)
    {
        KeyId = keyId;
        Market = market;
    }

    public string KeyId { get; }

    public MarketType Market { get; }

    public List<AssetBalance> Balances { get; } = [new AssetBalance("USDT", 10_000m, 0m, 10_000m, null)];

    public FuturesAccountSummary Summary { get; set; } = new(10_000m, 10_000m, 0m, 10_000m, DateTimeOffset.UnixEpoch);

    public PositionMode PositionMode { get; set; } = PositionMode.OneWay;

    public Dictionary<string, FuturesPosition> Positions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> Leverage { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, MarginType> Margins { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ExchangeOrder> Orders { get; } = [];

    public List<OcoOrderList> OcoLists { get; } = [];

    public List<ConditionalOrder> Conditional { get; } = [];

    public List<UserTrade> Trades { get; } = [];

    public List<FundingPayment> Funding { get; } = [];

    public List<OrderRequest> PlacedOrders { get; } = [];

    public List<SpotOcoRequest> PlacedOcos { get; } = [];

    public List<ConditionalOrderRequest> PlacedConditional { get; } = [];

    public List<(string Symbol, OrderRef Order)> CancelledOrders { get; } = [];

    public List<string> CancelledOcos { get; } = [];

    public List<OrderRef> CancelledConditional { get; } = [];

    public List<(string Symbol, int Leverage)> LeverageSets { get; } = [];

    public List<(string Symbol, MarginType Margin)> MarginSets { get; } = [];

    public int PositionModeReads { get; private set; }

    /// <summary>Return an exception to script a failure of the next placement; the request is still recorded.</summary>
    public Func<OrderRequest, Exception?>? OnPlaceOrder { get; set; }

    public Func<SpotOcoRequest, Exception?>? OnPlaceOco { get; set; }

    public Func<ConditionalOrderRequest, Exception?>? OnPlaceConditional { get; set; }

    public Func<OrderRef, Exception?>? OnCancel { get; set; }

    /// <summary>The leverage the exchange applies; defaults to the one asked for.</summary>
    public Func<string, int, int>? OnSetLeverage { get; set; }

    public bool FillMarketOrders { get; set; } = true;

    public decimal MarketPrice { get; set; } = 100m;

    public decimal FeeRate { get; set; } = 0.001m;

    public StreamHealth Health { get; set; } = StreamHealth.Connected;

    public int SubscriberCount
    {
        get
        {
            lock (_lock)
                return _subscribers.Count;
        }
    }

    public DateTimeOffset Now { get; set; } = new(2026, 1, 5, 0, 0, 5, TimeSpan.Zero);

    // --- orders ---------------------------------------------------------------------------------------------------

    public ExchangeOrder Place(OrderRequest request)
    {
        lock (_lock)
        {
            PlacedOrders.Add(request);
            if (OnPlaceOrder?.Invoke(request) is { } error)
                throw error;
            if (Orders.Any(o => o.ClientOrderId == request.ClientOrderId))
                throw new ExchangeRequestException($"Duplicate client order id {request.ClientOrderId}", ExchangeErrorKind.DuplicateClientOrderId, -2026);
            var fills = request.Type == OrderType.Market && FillMarketOrders;
            var quantity = request.Quantity ?? 0m;
            var order = new ExchangeOrder(request.Symbol, _nextId++, request.ClientOrderId, request.Side, request.Type, fills ? OrderStatus.Filled : OrderStatus.New,
                request.TimeInForce ?? TimeInForce.GoodTillCanceled, request.Price ?? 0m, request.StopPrice, quantity, fills ? quantity : 0m, fills ? quantity * MarketPrice : 0m,
                fills ? MarketPrice : null, request.ReduceOnly, null, Now, Now);
            Orders.Add(order);
            if (fills)
                Trades.Add(new UserTrade(request.Symbol, _nextTradeId++, order.OrderId, MarketPrice, quantity, quantity * MarketPrice, quantity * MarketPrice * FeeRate, "USDT",
                    request.Side == OrderSide.Buy, false, Market == MarketType.UsdFutures ? 0m : null, Now));
            return order;
        }
    }

    public ExchangeOrder Get(string symbol, OrderRef reference)
    {
        lock (_lock)
            return Find(reference) ?? throw new ExchangeRequestException($"Unknown order {reference}", ExchangeErrorKind.UnknownOrder, -2013);
    }

    public IReadOnlyList<ExchangeOrder> Open(string? symbol)
    {
        lock (_lock)
            return Orders.Where(o => o.Status.IsOpen() && (symbol is null || string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public ExchangeOrder Cancel(string symbol, OrderRef reference)
    {
        lock (_lock)
        {
            CancelledOrders.Add((symbol, reference));
            if (OnCancel?.Invoke(reference) is { } error)
                throw error;
            var order = Find(reference) ?? throw new ExchangeRequestException($"Unknown order {reference}", ExchangeErrorKind.UnknownOrder, -2011);
            if (!order.Status.IsOpen())
                throw new ExchangeRequestException($"Order {reference} is not open", ExchangeErrorKind.UnknownOrder, -2011);
            return Replace(order with { Status = OrderStatus.Canceled, UpdatedAt = Now });
        }
    }

    public IReadOnlyList<ExchangeOrder> CancelAll(string symbol)
    {
        lock (_lock)
        {
            var open = Open(symbol);
            foreach (var order in open)
                Replace(order with { Status = OrderStatus.Canceled, UpdatedAt = Now });
            return open;
        }
    }

    public IReadOnlyList<UserTrade> TradesSince(string symbol, DateTimeOffset? from, long? fromTradeId, int? limit)
    {
        lock (_lock)
            return Trades.Where(t => string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase) && (from is null || t.Timestamp >= from) && (fromTradeId is null || t.TradeId >= fromTradeId))
                .OrderBy(t => t.TradeId).Take(limit ?? 1000).ToList();
    }

    // --- spot OCO -------------------------------------------------------------------------------------------------

    public OcoOrderList PlaceOco(SpotOcoRequest request)
    {
        lock (_lock)
        {
            PlacedOcos.Add(request);
            if (OnPlaceOco?.Invoke(request) is { } error)
                throw error;
            if (OcoLists.Any(l => l.ListClientOrderId == request.ListClientOrderId) || Orders.Any(o => o.ClientOrderId == request.TakeProfitClientOrderId || o.ClientOrderId == request.StopClientOrderId))
                throw new ExchangeRequestException($"Duplicate client order id {request.ListClientOrderId}", ExchangeErrorKind.DuplicateClientOrderId, -2026);
            var listId = _nextId++;
            var tp = new ExchangeOrder(request.Symbol, _nextId++, request.TakeProfitClientOrderId, request.Side, OrderType.LimitMaker, OrderStatus.New, TimeInForce.GoodTillCanceled,
                request.TakeProfitPrice, null, request.Quantity, 0m, 0m, null, false, listId, Now, Now);
            var sl = new ExchangeOrder(request.Symbol, _nextId++, request.StopClientOrderId, request.Side, OrderType.StopLossLimit, OrderStatus.New, TimeInForce.GoodTillCanceled,
                request.StopLimitPrice, request.StopPrice, request.Quantity, 0m, 0m, null, false, listId, Now, Now);
            Orders.Add(tp);
            Orders.Add(sl);
            var list = new OcoOrderList(listId, request.ListClientOrderId, request.Symbol, OcoListStatus.Executing, [new OrderRef(tp.OrderId, tp.ClientOrderId), new OrderRef(sl.OrderId, sl.ClientOrderId)], [tp, sl], Now);
            OcoLists.Add(list);
            return list;
        }
    }

    public OcoOrderList GetOco(string listClientOrderId)
    {
        lock (_lock)
            return OcoLists.FirstOrDefault(l => l.ListClientOrderId == listClientOrderId) ?? throw new ExchangeRequestException($"Unknown OCO {listClientOrderId}", ExchangeErrorKind.UnknownOrder, -2013);
    }

    public OcoOrderList CancelOco(string symbol, string listClientOrderId)
    {
        lock (_lock)
        {
            CancelledOcos.Add(listClientOrderId);
            var index = OcoLists.FindIndex(l => l.ListClientOrderId == listClientOrderId);
            if (index < 0)
                throw new ExchangeRequestException($"Unknown OCO {listClientOrderId}", ExchangeErrorKind.UnknownOrder, -2011);
            var list = OcoLists[index];
            if (list.Status != OcoListStatus.Executing)
                throw new ExchangeRequestException($"OCO {listClientOrderId} is not open", ExchangeErrorKind.UnknownOrder, -2011);
            foreach (var leg in list.Orders)
            {
                if (Find(leg) is { } order && order.Status.IsOpen())
                    Replace(order with { Status = OrderStatus.Canceled, UpdatedAt = Now });
            }

            var done = list with { Status = OcoListStatus.AllDone, OrderReports = null };
            OcoLists[index] = done;
            return done;
        }
    }

    // --- futures conditional orders -------------------------------------------------------------------------------

    public ConditionalOrder PlaceConditional(ConditionalOrderRequest request)
    {
        lock (_lock)
        {
            PlacedConditional.Add(request);
            if (OnPlaceConditional?.Invoke(request) is { } error)
                throw error;
            if (PositionMode == PositionMode.Hedge)
                throw new ExchangeRequestException("Order's position side does not match user's setting.", ExchangeErrorKind.IncorrectState, -4061);
            if (Conditional.Any(o => o.ClientAlgoId == request.ClientAlgoId))
                throw new ExchangeRequestException($"Duplicate client algo id {request.ClientAlgoId}", ExchangeErrorKind.DuplicateClientOrderId, -2026);
            var order = new ConditionalOrder(request.Symbol, _nextId++, request.ClientAlgoId, request.Side, request.Type, ConditionalOrderStatus.New, request.TriggerPrice, request.Price,
                request.Quantity, request.ReduceOnly, request.ClosePosition, null, null, null, Now, Now, null);
            Conditional.Add(order);
            return order;
        }
    }

    public ConditionalOrder GetConditional(OrderRef reference)
    {
        lock (_lock)
            return FindConditional(reference) ?? throw new ExchangeRequestException($"Unknown conditional order {reference}", ExchangeErrorKind.UnknownOrder, -2013);
    }

    public ConditionalOrder CancelConditional(OrderRef reference)
    {
        lock (_lock)
        {
            CancelledConditional.Add(reference);
            if (OnCancel?.Invoke(reference) is { } error)
                throw error;
            var order = FindConditional(reference) ?? throw new ExchangeRequestException($"Unknown conditional order {reference}", ExchangeErrorKind.UnknownOrder, -2011);
            if (!order.Status.IsOpen())
                throw new ExchangeRequestException($"Conditional order {reference} is not open", ExchangeErrorKind.UnknownOrder, -2011);
            return ReplaceConditional(order with { Status = ConditionalOrderStatus.Canceled, UpdatedAt = Now });
        }
    }

    public IReadOnlyList<ConditionalOrder> OpenConditional(string? symbol)
    {
        lock (_lock)
            return Conditional.Where(o => o.Status.IsOpen() && (symbol is null || string.Equals(o.Symbol, symbol, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // --- the test drives fills --------------------------------------------------------------------------------------

    public IDisposable Subscribe(Func<UserDataEvent, CancellationToken, ValueTask> handler)
    {
        lock (_lock)
            _subscribers.Add(handler);
        return new Unsubscriber(this, handler);
    }

    public async Task PushAsync(UserDataEvent e)
    {
        List<Func<UserDataEvent, CancellationToken, ValueTask>> handlers;
        lock (_lock)
            handlers = _subscribers.ToList();
        foreach (var handler in handlers)
            await handler(e, CancellationToken.None);
    }

    /// <summary>Fills a regular order (fully, or <paramref name="quantity"/> of it) at a price, records the trade and pushes the order update with its fill.</summary>
    public async Task<ExchangeOrder> FillAsync(string clientOrderId, decimal price, decimal? quantity = null, decimal? fee = null, string feeAsset = "USDT", decimal? realizedPnl = null)
    {
        ExchangeOrder filled;
        UserTrade trade;
        lock (_lock)
        {
            var order = Find(OrderRef.ByClientId(clientOrderId)) ?? throw new InvalidOperationException($"No order {clientOrderId}.");
            var taken = Math.Min(quantity ?? order.Quantity - order.FilledQuantity, order.Quantity - order.FilledQuantity);
            var cumulative = order.FilledQuantity + taken;
            var average = order.AverageFillPrice is { } avg && order.FilledQuantity > 0 ? (avg * order.FilledQuantity + price * taken) / cumulative : price;
            filled = Replace(order with
            {
                Status = cumulative >= order.Quantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled, FilledQuantity = cumulative, FilledQuoteQuantity = cumulative * average, AverageFillPrice = average, UpdatedAt = Now,
            });
            trade = new UserTrade(order.Symbol, _nextTradeId++, order.OrderId, price, taken, taken * price, fee ?? taken * price * FeeRate, feeAsset, order.Side == OrderSide.Buy, false,
                realizedPnl ?? (Market == MarketType.UsdFutures ? 0m : null), Now);
            Trades.Add(trade);
            if (filled.Status == OrderStatus.Filled && filled.OrderListId is { } listId)
            {
                // The other OCO leg expires on the exchange.
                foreach (var sibling in Orders.Where(o => o.OrderListId == listId && o.OrderId != filled.OrderId && o.Status.IsOpen()).ToList())
                    Replace(sibling with { Status = OrderStatus.Expired, UpdatedAt = Now });
                var index = OcoLists.FindIndex(l => l.OrderListId == listId);
                if (index >= 0)
                    OcoLists[index] = OcoLists[index] with { Status = OcoListStatus.AllDone };
            }
        }

        await PushAsync(new UserDataEvent.OrderUpdate(KeyId, Market, Now, filled, ExecutionType.Trade, trade));
        return filled;
    }

    /// <summary>Triggers a conditional order and fills the actual order at a price: the ALGO_UPDATE sequence TRIGGERED then FINISHED, plus the actual order's trade.</summary>
    public async Task<ConditionalOrder> TriggerAsync(string clientAlgoId, decimal price, decimal? fee = null, decimal? realizedPnl = null)
    {
        ConditionalOrder triggered;
        ConditionalOrder finished;
        lock (_lock)
        {
            var order = FindConditional(OrderRef.ByClientId(clientAlgoId)) ?? throw new InvalidOperationException($"No conditional order {clientAlgoId}.");
            var actualId = _nextId++;
            var quantity = order.Quantity ?? Math.Abs(Positions.TryGetValue(order.Symbol, out var p) ? p.Quantity : 0m);
            triggered = ReplaceConditional(order with { Status = ConditionalOrderStatus.Triggered, ActualOrderId = actualId, TriggeredAt = Now, UpdatedAt = Now });
            Trades.Add(new UserTrade(order.Symbol, _nextTradeId++, actualId, price, quantity, quantity * price, fee ?? quantity * price * FeeRate, "USDT", order.Side == OrderSide.Buy, false, realizedPnl ?? 0m, Now));
            finished = ReplaceConditional(triggered with { Status = ConditionalOrderStatus.Finished, FilledQuantity = quantity, AverageFillPrice = price, UpdatedAt = Now });
        }

        await PushAsync(new UserDataEvent.ConditionalOrderUpdate(KeyId, Market, Now, triggered));
        await PushAsync(new UserDataEvent.ConditionalOrderUpdate(KeyId, Market, Now, finished));
        return finished;
    }

    public async Task ExpireAsync(string clientOrderId)
    {
        ExchangeOrder expired;
        lock (_lock)
        {
            var order = Find(OrderRef.ByClientId(clientOrderId)) ?? throw new InvalidOperationException($"No order {clientOrderId}.");
            expired = Replace(order with { Status = OrderStatus.Expired, UpdatedAt = Now });
        }

        await PushAsync(new UserDataEvent.OrderUpdate(KeyId, Market, Now, expired, ExecutionType.Expired, null));
    }

    public void SetPosition(string symbol, decimal signedQuantity, decimal entryPrice, int leverage = 1, MarginType margin = MarginType.Isolated)
    {
        lock (_lock)
        {
            if (signedQuantity == 0)
                Positions.Remove(symbol);
            else
                Positions[symbol] = new FuturesPosition(symbol, signedQuantity, entryPrice, entryPrice, 0m, null, leverage, margin, null, Math.Abs(signedQuantity * entryPrice), Now);
        }
    }

    public UserDataEvent.Snapshot Snapshot()
    {
        lock (_lock)
            return new UserDataEvent.Snapshot(KeyId, Market, Now, Open(null), Market == MarketType.UsdFutures ? Positions.Values.ToList() : null, Balances.ToList(), Market == MarketType.UsdFutures ? OpenConditional(null) : null);
    }

    private ExchangeOrder? Find(OrderRef reference) =>
        Orders.FirstOrDefault(o => reference.OrderId is { } id ? o.OrderId == id : o.ClientOrderId == reference.ClientOrderId);

    private ConditionalOrder? FindConditional(OrderRef reference) =>
        Conditional.FirstOrDefault(o => reference.OrderId is { } id ? o.AlgoId == id : o.ClientAlgoId == reference.ClientOrderId);

    private ExchangeOrder Replace(ExchangeOrder order)
    {
        var index = Orders.FindIndex(o => o.OrderId == order.OrderId);
        Orders[index] = order;
        return order;
    }

    private ConditionalOrder ReplaceConditional(ConditionalOrder order)
    {
        var index = Conditional.FindIndex(o => o.AlgoId == order.AlgoId);
        Conditional[index] = order;
        return order;
    }

    private sealed class Unsubscriber(FakeExchangeAccount owner, Func<UserDataEvent, CancellationToken, ValueTask> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lock)
                owner._subscribers.Remove(handler);
        }
    }
}
