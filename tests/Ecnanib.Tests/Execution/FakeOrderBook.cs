using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Ecnanib.Tests.Exchange;
using Ecnanib.Tests.Identity;

namespace Ecnanib.Tests.Execution;

/// <summary>
/// Binance's order books behind <see cref="FakeBinance"/>, for BTCUSDT on Spot and Futures: orders, Futures conditional
/// orders and Spot OCO lists, kept by client order ID, so placing, reading and cancelling agree. Market orders fill at
/// once at <see cref="Price"/>. Everything else rests until a test fills or triggers it. A test can make the next calls
/// to an endpoint fail, and say whether Binance took the order anyway, which is what a timeout or a 5XX hides
/// (FR-EXE-05). Positions, trades and the Spot balance follow the fills.
/// </summary>
internal sealed class FakeOrderBook
{
    public const string SpotOrder = "/api/v3/order";
    public const string SpotList = "/api/v3/orderList/oco";
    public const string SpotListQuery = "/api/v3/orderList";
    public const string SpotTrades = "/api/v3/myTrades";
    public const string SpotAccount = "/api/v3/account";
    public const string FuturesOrder = "/fapi/v1/order";
    public const string FuturesAlgo = "/fapi/v1/algoOrder";
    public const string FuturesPosition = "/fapi/v3/positionRisk";
    public const string FuturesTrades = "/fapi/v1/userTrades";

    private readonly ConcurrentDictionary<string, Order> _orders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OrderList> _lists = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Fault?>> _faults = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Sent> _sent = new();
    private readonly TestClock _clock;
    private long _ids = 1000;

    public FakeOrderBook(FakeBinance binance, TestClock clock)
    {
        _clock = clock;

        binance.On(HttpMethod.Post, SpotOrder, request => Place(request, futures: false, algo: false));
        binance.On(HttpMethod.Get, SpotOrder, request => Read(request, request.Parameter("origClientOrderId")!, futures: false));
        binance.On(HttpMethod.Delete, SpotOrder, request => Cancel(request, request.Parameter("origClientOrderId")!, futures: false));
        binance.On(HttpMethod.Post, SpotList, PlaceList);
        binance.On(HttpMethod.Get, SpotListQuery, request => ReadList(request, request.Parameter("origClientOrderId")!));
        binance.On(HttpMethod.Delete, SpotListQuery, request => CancelList(request, request.Parameter("listClientOrderId")!));
        binance.On(HttpMethod.Get, SpotTrades, request => Trades(request, futures: false));
        binance.On(HttpMethod.Get, SpotAccount, request => Account(request));

        binance.On(HttpMethod.Post, FuturesOrder, request => Place(request, futures: true, algo: false));
        binance.On(HttpMethod.Get, FuturesOrder, request => Read(request, request.Parameter("origClientOrderId")!, futures: true));
        binance.On(HttpMethod.Delete, FuturesOrder, request => Cancel(request, request.Parameter("origClientOrderId")!, futures: true));
        binance.On(HttpMethod.Post, FuturesAlgo, request => Place(request, futures: true, algo: true));
        binance.On(HttpMethod.Get, FuturesAlgo, request => Read(request, request.Parameter("clientAlgoId")!, futures: true));
        binance.On(HttpMethod.Delete, FuturesAlgo, request => Cancel(request, request.Parameter("clientAlgoId")!, futures: true));
        binance.On(HttpMethod.Get, FuturesPosition, Position);
        binance.On(HttpMethod.Get, FuturesTrades, request => Trades(request, futures: true));

        // The Futures symbol is ready at the default leverage, so FR-RISK-09's setup changes nothing.
        binance.On(HttpMethod.Get, "/fapi/v1/symbolConfig", _ => FakeBinance.Json(BinanceSamples.SymbolConfig("ISOLATED", leverage: 2)));
        binance.On(HttpMethod.Post, "/fapi/v1/leverage", request => FakeBinance.Json(BinanceSamples.Leverage(int.Parse(request.Parameter("leverage")!, CultureInfo.InvariantCulture))));
        binance.On(HttpMethod.Post, "/fapi/v1/marginType", _ => FakeBinance.Json(BinanceSamples.MarginTypeChanged));
    }

    /// <summary>What market orders fill at.</summary>
    public decimal Price { get; set; } = 60_000m;

    /// <summary>Spot's commission on a buy, taken from the base asset bought.</summary>
    public decimal SpotFee { get; set; } = 0.001m;

    /// <summary>Base asset the Spot account holds apart from the agent's fills, as if the user owned some.</summary>
    public decimal SpotBaseOwned { get; set; }

    /// <summary>A Futures position the agent didn't open, such as the user's own.</summary>
    public decimal FuturesPositionOwned { get; set; }

    /// <summary>Every order, list and close sent, in order, with Binance's answer, whether it failed or not.</summary>
    public IReadOnlyList<Sent> Placed => [.. _sent];

    public Order? Find(string clientOrderId) => _orders.GetValueOrDefault(clientOrderId);

    public IReadOnlyList<Order> Orders => [.. _orders.Values.OrderBy(order => order.Id)];

    /// <summary>How many times an order or list with this client order ID was sent: AC-14 allows one.</summary>
    public int SendsOf(string clientOrderId) => _sent.Count(sent => sent.ClientOrderId == clientOrderId);

    /// <summary>The next calls to the endpoint fail as given, one fault a call, then it answers again. A null lets a call through.</summary>
    public void Fail(HttpMethod method, string path, params Fault?[] faults)
    {
        var queue = _faults.GetOrAdd($"{method.Method} {path}", _ => new ConcurrentQueue<Fault?>());

        foreach (var fault in faults)
        {
            queue.Enqueue(fault);
        }
    }

    /// <summary>An order Binance took and filled at once, without its answer ever arriving, as before a crash.</summary>
    public void Seed(string clientOrderId, bool futures, string side, decimal quantity)
    {
        _orders[clientOrderId] = new Order(clientOrderId, Interlocked.Increment(ref _ids), futures, false, side, "MARKET", quantity)
        {
            UpdatedAt = _clock.GetUtcNow(),
        };
        Fill(clientOrderId, price: Price);
    }

    /// <summary>What was sent to one endpoint, in order.</summary>
    public IReadOnlyList<Sent> SentTo(string path) => [.. _sent.Where(sent => sent.Path == path)];

    /// <summary>Fills a resting order, by <paramref name="quantity"/> or all of what's left.</summary>
    public void Fill(string clientOrderId, decimal? quantity = null, decimal? price = null)
    {
        var order = _orders[clientOrderId];

        lock (order)
        {
            var fill = Math.Min(quantity ?? order.Quantity - order.Executed, order.Quantity - order.Executed);
            order.Fills.Add((fill, price ?? order.Price ?? Price, _clock.GetUtcNow()));
            order.Executed += fill;
            order.Status = order.Executed >= order.Quantity ? "FILLED" : "PARTIALLY_FILLED";
            order.UpdatedAt = _clock.GetUtcNow();
        }
    }

    /// <summary>A stop or target whose trigger price was reached, and which filled.</summary>
    public void Trigger(string clientOrderId)
    {
        var order = _orders[clientOrderId];
        Fill(clientOrderId, price: order.StopPrice ?? order.Price);

        if (order.Algo)
        {
            order.Status = "FINISHED";
        }

        if (order.ListId is { } listId)
        {
            foreach (var other in _lists[listId].Orders.Where(other => other != clientOrderId))
            {
                _orders[other].Status = "EXPIRED";
            }
        }
    }

    /// <summary>The Futures position from the agent's fills, and the user's own.</summary>
    public decimal FuturesAmount => FuturesPositionOwned + _orders.Values
        .Where(order => order.Futures && !order.Algo || order.Algo && order.Status == "FINISHED")
        .Sum(order => order.Side == "BUY" ? order.Executed : -order.Executed);

    /// <summary>What the Spot account holds of BTC: what the user owned, and the fills less Spot's fee.</summary>
    public decimal SpotBase => SpotBaseOwned + _orders.Values
        .Where(order => !order.Futures)
        .Sum(order => order.Side == "BUY" ? order.Executed * (1 - SpotFee) : -order.Executed);

    /// <summary>What resting Spot sells lock of BTC.</summary>
    private decimal SpotLocked => _orders.Values
        .Where(order => !order.Futures && order.Side == "SELL" && order.Status is "NEW" or "PARTIALLY_FILLED")
        .GroupBy(order => order.ListId ?? order.ClientOrderId)
        .Sum(group => group.Max(order => order.Quantity - order.Executed));

    private HttpResponseMessage Place(RecordedRequest request, bool futures, bool algo)
    {
        var id = algo ? request.Parameter("clientAlgoId")! : request.Parameter("newClientOrderId")!;
        var fault = Next(request);
        var sent = new Sent(request.Path, id, _clock.GetUtcNow(), request.Parameter("type")!, request.Parameter("side")!, Decimal(request.Parameter("quantity"))!.Value)
        {
            StopPrice = Decimal(request.Parameter(algo ? "triggerPrice" : "stopPrice")),
            ReduceOnly = request.Parameter("reduceOnly") == "true",
            WorkingType = request.Parameter("workingType"),
            RecvWindow = request.Parameter("recvWindow"),
        };
        _sent.Enqueue(sent);

        if (fault is { Accept: false })
        {
            return fault.Respond!();
        }

        if (_orders.ContainsKey(id))
        {
            return futures
                ? FakeBinance.Error(HttpStatusCode.BadRequest, algo ? -4116 : -4116, "ClientOrderId is duplicated.")
                : FakeBinance.Error(HttpStatusCode.BadRequest, -2010, "Duplicate order sent.");
        }

        var type = request.Parameter("type")!;
        var order = new Order(id, Interlocked.Increment(ref _ids), futures, algo, request.Parameter("side")!, type, sent.Quantity)
        {
            Price = Decimal(request.Parameter("price")),
            StopPrice = sent.StopPrice,
            Status = "NEW",
            UpdatedAt = _clock.GetUtcNow(),
        };

        if (!futures && order.Side == "SELL" && order.Quantity > SpotBase - SpotLocked)
        {
            return FakeBinance.Error(HttpStatusCode.BadRequest, -2010, "Account has insufficient balance for requested action.");
        }

        if (futures && order.Type == "MARKET" && sent.ReduceOnly && Math.Abs(FuturesAmount) < order.Quantity)
        {
            return FakeBinance.Error(HttpStatusCode.BadRequest, -2022, "ReduceOnly Order is rejected.");
        }

        _orders[id] = order;

        if (type == "MARKET")
        {
            Fill(id, price: Price);
        }

        if (fault is { AnswerAsNew: true })
        {
            // Binance's answer was written before the order filled.
            return FakeBinance.Json(Json(new Order(order.ClientOrderId, order.Id, order.Futures, order.Algo, order.Side, order.Type, order.Quantity)
            {
                Price = order.Price,
                StopPrice = order.StopPrice,
                Status = "NEW",
                UpdatedAt = order.UpdatedAt,
            }));
        }

        return fault?.Respond?.Invoke() ?? FakeBinance.Json(Json(order));
    }

    private HttpResponseMessage PlaceList(RecordedRequest request)
    {
        var listId = request.Parameter("listClientOrderId")!;
        var aboveId = request.Parameter("aboveClientOrderId")!;
        var belowId = request.Parameter("belowClientOrderId")!;
        var quantity = Decimal(request.Parameter("quantity"))!.Value;
        var fault = Next(request);
        _sent.Enqueue(new Sent(request.Path, listId, _clock.GetUtcNow(), "OCO", request.Parameter("side")!, quantity)
        {
            StopPrice = Decimal(request.Parameter("belowStopPrice")),
            Price = Decimal(request.Parameter("abovePrice")),
            RecvWindow = request.Parameter("recvWindow"),
        });

        if (fault is { Accept: false })
        {
            return fault.Respond!();
        }

        if (_lists.ContainsKey(listId))
        {
            return FakeBinance.Error(HttpStatusCode.BadRequest, -2010, "Duplicate order sent.");
        }

        if (request.Parameter("side") == "SELL" && quantity > SpotBase - SpotLocked)
        {
            return FakeBinance.Error(HttpStatusCode.BadRequest, -2010, "Account has insufficient balance for requested action.");
        }

        var target = new Order(aboveId, Interlocked.Increment(ref _ids), false, false, request.Parameter("side")!, request.Parameter("aboveType")!, quantity)
        {
            Price = Decimal(request.Parameter("abovePrice")),
            Status = "NEW",
            ListId = listId,
            UpdatedAt = _clock.GetUtcNow(),
        };
        var stop = new Order(belowId, Interlocked.Increment(ref _ids), false, false, request.Parameter("side")!, request.Parameter("belowType")!, quantity)
        {
            StopPrice = Decimal(request.Parameter("belowStopPrice")),
            Status = "NEW",
            ListId = listId,
            UpdatedAt = _clock.GetUtcNow(),
        };

        _orders[aboveId] = target;
        _orders[belowId] = stop;
        _lists[listId] = new OrderList(listId, Interlocked.Increment(ref _ids), [aboveId, belowId]);

        return fault?.Respond?.Invoke() ?? FakeBinance.Json(ListJson(_lists[listId]));
    }

    private HttpResponseMessage Read(RecordedRequest request, string id, bool futures)
    {
        if (Next(request) is { Respond: { } respond })
        {
            return respond();
        }

        return _orders.TryGetValue(id, out var order) && order.Futures == futures
            ? FakeBinance.Json(Json(order))
            : FakeBinance.Error(HttpStatusCode.BadRequest, -2013, "Order does not exist.");
    }

    private HttpResponseMessage ReadList(RecordedRequest request, string id)
    {
        if (Next(request) is { Respond: { } respond })
        {
            return respond();
        }

        return _lists.TryGetValue(id, out var list)
            ? FakeBinance.Json(ListJson(list))
            : FakeBinance.Error(HttpStatusCode.BadRequest, -2013, "Order list does not exist.");
    }

    private HttpResponseMessage Cancel(RecordedRequest request, string id, bool futures)
    {
        if (Next(request) is { Respond: { } respond })
        {
            return respond();
        }

        if (!_orders.TryGetValue(id, out var order) || order.Futures != futures || order.Status is not ("NEW" or "PARTIALLY_FILLED"))
        {
            return FakeBinance.Error(HttpStatusCode.BadRequest, -2011, "Unknown order sent.");
        }

        order.Status = "CANCELED";
        order.UpdatedAt = _clock.GetUtcNow();

        return FakeBinance.Json(Json(order));
    }

    private HttpResponseMessage CancelList(RecordedRequest request, string id)
    {
        if (Next(request) is { Respond: { } respond })
        {
            return respond();
        }

        if (!_lists.TryGetValue(id, out var list) || list.Orders.All(order => _orders[order].Status is not ("NEW" or "PARTIALLY_FILLED")))
        {
            return FakeBinance.Error(HttpStatusCode.BadRequest, -2011, "Unknown order list sent.");
        }

        foreach (var order in list.Orders.Select(order => _orders[order]))
        {
            order.Status = "CANCELED";
        }

        return FakeBinance.Json(ListJson(list));
    }

    private HttpResponseMessage Trades(RecordedRequest request, bool futures)
    {
        var orderId = long.Parse(request.Parameter("orderId")!, CultureInfo.InvariantCulture);
        var order = _orders.Values.FirstOrDefault(order => order.Id == orderId && order.Futures == futures);

        if (order is null)
        {
            return FakeBinance.Json("[]");
        }

        var trades = order.Fills.Select((fill, index) => futures
            ? $$"""{"buyer":{{Bool(order.Side == "BUY")}},"commission":"{{N(fill.Quantity * fill.Price * 0.0004m)}}","commissionAsset":"USDT","id":{{orderId * 10 + index}},"maker":false,"orderId":{{orderId}},"price":"{{N(fill.Price)}}","qty":"{{N(fill.Quantity)}}","quoteQty":"{{N(fill.Quantity * fill.Price)}}","realizedPnl":"0","side":"{{order.Side}}","positionSide":"BOTH","symbol":"BTCUSDT","time":{{fill.At.ToUnixTimeMilliseconds()}}}"""
            : $$"""{"symbol":"BTCUSDT","id":{{orderId * 10 + index}},"orderId":{{orderId}},"orderListId":-1,"price":"{{N(fill.Price)}}","qty":"{{N(fill.Quantity)}}","quoteQty":"{{N(fill.Quantity * fill.Price)}}","commission":"{{N(order.Side == "BUY" ? fill.Quantity * SpotFee : fill.Quantity * fill.Price * SpotFee)}}","commissionAsset":"{{(order.Side == "BUY" ? "BTC" : "USDT")}}","time":{{fill.At.ToUnixTimeMilliseconds()}},"isBuyer":{{Bool(order.Side == "BUY")}},"isMaker":false,"isBestMatch":true}""");

        return FakeBinance.Json($"[{string.Join(",", trades)}]");
    }

    private HttpResponseMessage Account(RecordedRequest request)
    {
        if (Next(request) is { Respond: { } respond })
        {
            return respond();
        }

        var locked = SpotLocked;

        return FakeBinance.Json($$"""
            {"makerCommission":10,"canTrade":true,"canWithdraw":false,"canDeposit":true,"accountType":"SPOT",
             "balances":[{"asset":"BTC","free":"{{N(SpotBase - locked)}}","locked":"{{N(locked)}}"},{"asset":"USDT","free":"10000.00000000","locked":"0.00000000"}],
             "permissions":["SPOT"],"uid":354937868}
            """);
    }

    private HttpResponseMessage Position(RecordedRequest request)
    {
        if (Next(request) is { Respond: { } respond })
        {
            return respond();
        }

        return FakeBinance.Json($$"""
            [{"symbol":"BTCUSDT","positionSide":"BOTH","positionAmt":"{{N(FuturesAmount)}}","entryPrice":"{{N(Price)}}","breakEvenPrice":"{{N(Price)}}",
              "markPrice":"{{N(Price)}}","unRealizedProfit":"0","liquidationPrice":"0","marginAsset":"USDT","updateTime":{{_clock.GetUtcNow().ToUnixTimeMilliseconds()}}}]
            """);
    }

    private Fault? Next(RecordedRequest request)
    {
        // A null in the queue lets that call through.
        if (!_faults.TryGetValue($"{request.Method.Method} {request.Path}", out var queue) || !queue.TryDequeue(out var fault) || fault is null)
        {
            return null;
        }

        fault.Then?.Invoke();

        return fault;
    }

    private string Json(Order order)
    {
        var updated = order.UpdatedAt.ToUnixTimeMilliseconds();
        var average = order.Executed > 0m ? order.Fills.Sum(fill => fill.Quantity * fill.Price) / order.Executed : 0m;

        if (order.Algo)
        {
            return $$"""
                {"algoId":{{order.Id}},"clientAlgoId":"{{order.ClientOrderId}}","algoType":"CONDITIONAL","orderType":"{{order.Type}}",
                 "symbol":"BTCUSDT","side":"{{order.Side}}","positionSide":"BOTH","quantity":"{{N(order.Quantity)}}","algoStatus":"{{order.Status}}",
                 "actualOrderId":"{{(order.Status == "FINISHED" ? (order.Id + 1).ToString(CultureInfo.InvariantCulture) : "")}}",
                 "triggerPrice":"{{N(order.StopPrice ?? 0m)}}","price":"0","actualQty":"{{N(order.Executed)}}","actualPrice":"{{N(average)}}",
                 "workingType":"MARK_PRICE","reduceOnly":true,"createTime":{{updated}},"updateTime":{{updated}}}
                """;
        }

        if (order.Futures)
        {
            return $$"""
                {"clientOrderId":"{{order.ClientOrderId}}","executedQty":"{{N(order.Executed)}}","orderId":{{order.Id}},"avgPrice":"{{N(average)}}",
                 "origQty":"{{N(order.Quantity)}}","price":"{{N(order.Price ?? 0m)}}","side":"{{order.Side}}","status":"{{order.Status}}",
                 "stopPrice":"0","symbol":"BTCUSDT","type":"{{order.Type}}","updateTime":{{updated}}}
                """;
        }

        return $$"""
            {"symbol":"BTCUSDT","orderId":{{order.Id}},"orderListId":-1,"clientOrderId":"{{order.ClientOrderId}}","transactTime":{{updated}},
             "price":"{{N(order.Price ?? 0m)}}","origQty":"{{N(order.Quantity)}}","executedQty":"{{N(order.Executed)}}",
             "cummulativeQuoteQty":"{{N(order.Executed * average)}}","status":"{{order.Status}}","type":"{{order.Type}}","side":"{{order.Side}}",
             "stopPrice":"{{N(order.StopPrice ?? 0m)}}","updateTime":{{updated}}}
            """;
    }

    private string ListJson(OrderList list)
    {
        var open = list.Orders.Any(id => _orders[id].Status is "NEW" or "PARTIALLY_FILLED");

        return $$"""
            {"orderListId":{{list.Id}},"contingencyType":"OCO","listStatusType":"{{(open ? "EXEC_STARTED" : "ALL_DONE")}}",
             "listOrderStatus":"{{(open ? "EXECUTING" : "ALL_DONE")}}","listClientOrderId":"{{list.ListClientOrderId}}","transactionTime":0,
             "symbol":"BTCUSDT","orders":[{{string.Join(",", list.Orders.Select(id => $$"""{"symbol":"BTCUSDT","orderId":{{_orders[id].Id}},"clientOrderId":"{{id}}"}"""))}}]}
            """;
    }

    private static decimal? Decimal(string? value) =>
        value is null ? null : decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string N(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>An order on the book.</summary>
    internal sealed class Order(string clientOrderId, long id, bool futures, bool algo, string side, string type, decimal quantity)
    {
        public string ClientOrderId { get; } = clientOrderId;

        public long Id { get; } = id;

        public bool Futures { get; } = futures;

        public bool Algo { get; } = algo;

        public string Side { get; } = side;

        public string Type { get; } = type;

        public decimal Quantity { get; } = quantity;

        public decimal? Price { get; init; }

        public decimal? StopPrice { get; init; }

        public string Status { get; set; } = "NEW";

        public decimal Executed { get; set; }

        public string? ListId { get; init; }

        public DateTimeOffset UpdatedAt { get; set; }

        public List<(decimal Quantity, decimal Price, DateTimeOffset At)> Fills { get; } = [];
    }

    private sealed record OrderList(string ListClientOrderId, long Id, IReadOnlyList<string> Orders);

    /// <summary>One order or list as it was sent.</summary>
    internal sealed record Sent(string Path, string ClientOrderId, DateTimeOffset At, string Type, string Side, decimal Quantity)
    {
        public decimal? StopPrice { get; init; }

        public decimal? Price { get; init; }

        public bool ReduceOnly { get; init; }

        public string? WorkingType { get; init; }

        public string? RecvWindow { get; init; }
    }
}

/// <summary>How the next call to an endpoint fails.</summary>
/// <param name="Respond">The answer, or null for the usual one, which only an accepted order can have.</param>
/// <param name="Accept">For an order: Binance took it, whatever the answer says, as a 5XX or a timeout can hide.</param>
/// <param name="Then">Runs as the call arrives, such as to move the clock on.</param>
internal sealed record Fault(Func<HttpResponseMessage>? Respond, bool Accept = false, Action? Then = null)
{
    /// <summary>The order was answered as new and unfilled, though it filled at once.</summary>
    public bool AnswerAsNew { get; init; }

    /// <summary>Binance takes a market order and fills it, but answers before the fill, as NEW.</summary>
    public static Fault AnsweredBeforeTheFill() => new(null, Accept: true) { AnswerAsNew = true };

    /// <summary>Binance takes the order and answers as usual, after <paramref name="delay"/>.</summary>
    public static Fault Slow(TimeSpan delay, Action? arrived = null) =>
        new(null, Accept: true, () =>
        {
            arrived?.Invoke();
            Thread.Sleep(delay);
        });

    /// <summary>A 503, and Binance took the order or not.</summary>
    public static Fault ServerError(bool accepted, Action? then = null) =>
        new(() => FakeBinance.Json("Service Unavailable.", HttpStatusCode.ServiceUnavailable), accepted, then);

    public static Fault Rejected(int code, string message, Action? then = null) =>
        new(() => FakeBinance.Error(HttpStatusCode.BadRequest, code, message), Accept: false, then);

    public static Fault Throttled(Action? then = null) =>
        new(() => FakeBinance.Error(HttpStatusCode.TooManyRequests, -1003, "Too many requests.", ("Retry-After", "1")), Accept: false, then);
}
