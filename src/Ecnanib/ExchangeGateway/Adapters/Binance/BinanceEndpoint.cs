using System.Globalization;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// One call of Binance's API that the gateway makes: the market whose address serves it, its
/// method, its path, its query, and what it costs of which limit on requests. These are all of them.
/// <see cref="BinanceCall"/> takes nothing else, and nothing outside this class can make one, so
/// what the gateway can send to Binance is this list. See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// Most of them read. The ones that do not place an order, or take one off the exchange
/// (FR-BIN-6): they are the calls of the orders, at the end of the list, and each says its
/// method. Two more set how a symbol of the Futures market is margined in the account of a
/// credential: isolated, and at a leverage (FR-BIN-F2). And two begin and renew the account
/// event feed of the Futures market (FR-BIN-7). None withdraws, deposits, or transfers
/// funds, between the Spot and the Futures wallet of a user included (FR-BIN-9): Binance keeps
/// those calls in its wallet API, under <c>sapi</c>, of which the gateway makes one call, which
/// reads the permissions of a key.
/// </para>
/// <para>
/// A call of the market data takes parameters of its caller: a symbol, and the length of a
/// candle. A call of an order takes the order. Such a call is made here as well, from the one of
/// the list that it is, with its parameters percent-encoded, which Binance requires. Every
/// parameter is in the query, also of a call that is no <c>GET</c>: Binance takes them there, and
/// the log of outbound calls leaves the query out.
/// </para>
/// </remarks>
internal sealed class BinanceEndpoint
{
    /// <summary>
    /// The rows that a read of candles asks for: as many as the gateway gives, and the candle
    /// that is in progress. It is the most that Binance counts as 2 of request weight on the
    /// Futures market, where 500 rows cost 5.
    /// </summary>
    public const int CandleRows = ExchangeCandles.Most + 1;

    /// <summary>
    /// The rows that a read of funding asks for, which is the most that Binance gives in one
    /// answer. An answer of so many may not be all of what was asked for.
    /// </summary>
    public const int FundingRows = 1000;

    /// <summary>The clock of the Spot market.</summary>
    public static readonly BinanceEndpoint SpotTime = new(Market.Spot, "api/v3/time", BinanceLimit.Spot, weight: 1);

    /// <summary>The clock of the Futures market.</summary>
    public static readonly BinanceEndpoint FuturesTime = new(Market.Futures, "fapi/v1/time", BinanceLimit.Futures, weight: 1);

    /// <summary>
    /// What an API key may do. It is of Binance's wallet API, which the address of the Spot market
    /// serves on the real exchange. The test environment has no such call.
    /// </summary>
    public static readonly BinanceEndpoint KeyPermissions =
        new(Market.Spot, "sapi/v1/account/apiRestrictions", BinanceLimit.KeyPermissions, weight: 1);

    /// <summary>The account of the Spot market: whether it may trade, and what it holds, without the assets it holds none of.</summary>
    public static readonly BinanceEndpoint SpotAccount =
        new(Market.Spot, "api/v3/account", BinanceLimit.Spot, weight: 20, "omitZeroBalances=true");

    /// <summary>
    /// The settings of the account of the Futures market, which say whether it may trade, and
    /// the modes that hold for the whole account: whether it keeps a long and a short position
    /// of a symbol apart, and whether it margins with more than one asset.
    /// </summary>
    public static readonly BinanceEndpoint FuturesAccountSettings =
        new(Market.Futures, "fapi/v1/accountConfig", BinanceLimit.Futures, weight: 5);

    /// <summary>What the account of the Futures market holds.</summary>
    public static readonly BinanceEndpoint FuturesBalances = new(Market.Futures, "fapi/v3/balance", BinanceLimit.Futures, weight: 5);

    // The calls of the market data. Each but the two lists of symbols takes parameters of its
    // caller, so each is made with one of the methods below, and none is sent as it is here.

    // The newest candles of a symbol. Binance counts 2 for them on the Spot market whatever
    // their number, and on the Futures market for 100 to 499 rows.
    private static readonly BinanceEndpoint SpotCandles = new(Market.Spot, "api/v3/klines", BinanceLimit.Spot, weight: 2);

    private static readonly BinanceEndpoint FuturesCandles = new(Market.Futures, "fapi/v1/klines", BinanceLimit.Futures, weight: 2);

    // The price that a symbol last traded at. Binance's own clients say of the one of the
    // Futures market that what its answer has counted is not accurate, and to ignore it.
    private static readonly BinanceEndpoint SpotPrice = new(Market.Spot, "api/v3/ticker/price", BinanceLimit.Spot, weight: 2);

    private static readonly BinanceEndpoint FuturesPrice =
        new(Market.Futures, "fapi/v2/ticker/price", BinanceLimit.Futures, weight: 1, reportsUsage: false);

    // Every symbol of a market with its rules, in one answer. On the Spot market it is asked
    // without the permission sets of each symbol, which are most of the answer and are not read.
    private static readonly BinanceEndpoint SpotSymbols =
        new(Market.Spot, "api/v3/exchangeInfo", BinanceLimit.Spot, weight: 20, "showPermissionSets=false");

    private static readonly BinanceEndpoint FuturesSymbols = new(Market.Futures, "fapi/v1/exchangeInfo", BinanceLimit.Futures, weight: 1);

    // The mark price of a symbol of the Futures market, with its funding rate and the time of
    // the next funding.
    private static readonly BinanceEndpoint FuturesMarkPrice =
        new(Market.Futures, "fapi/v1/premiumIndex", BinanceLimit.Futures, weight: 1);

    private static readonly BinanceEndpoint FuturesOpenInterest =
        new(Market.Futures, "fapi/v1/openInterest", BinanceLimit.Futures, weight: 1);

    // The positions of the account of the Futures market in a symbol. Binance answers with
    // those that are there: a symbol with no position and no order is no row.
    private static readonly BinanceEndpoint FuturesPositions =
        new(Market.Futures, "fapi/v3/positionRisk", BinanceLimit.Futures, weight: 5);

    // What the account of the Futures market was paid as funding, and what it paid: the rows of
    // its income that are funding, in one symbol and between two moments (FR-BIN-F4). It only
    // reads. It is the heaviest call of the market: the part of Binance's limit that the service
    // allows itself must hold it, which is why Binance:LimitSharePercent starts at 2.
    private static readonly BinanceEndpoint FuturesFunding =
        new(Market.Futures, "fapi/v1/income", BinanceLimit.Futures, weight: 30, "incomeType=FUNDING_FEE");

    // The calls of the orders. Each takes the order of its caller, so each is made with one of
    // the methods below. The weights are what Binance documents: placing an order on the Futures
    // market costs nothing of the limit on requests, and is counted by the order.

    // An order of the Spot market: placed, at the market, at a price, or as a stop, asked about,
    // and taken off the exchange.
    private static readonly BinanceEndpoint SpotOrder =
        new(Market.Spot, "api/v3/order", BinanceLimit.Spot, weight: 1, method: HttpMethod.Post);

    private static readonly BinanceEndpoint SpotOrderStatus = new(Market.Spot, "api/v3/order", BinanceLimit.Spot, weight: 4);

    private static readonly BinanceEndpoint SpotOrderCancel =
        new(Market.Spot, "api/v3/order", BinanceLimit.Spot, weight: 1, method: HttpMethod.Delete);

    // The executions of one order of the Spot market. Asked with the order, it costs 5.
    private static readonly BinanceEndpoint SpotTrades = new(Market.Spot, "api/v3/myTrades", BinanceLimit.Spot, weight: 5);

    // The pair of the Spot market: two orders that Binance holds as one list, of which it ends
    // one when the other is triggered. It is placed with one call, and taken off with one.
    private static readonly BinanceEndpoint SpotPair =
        new(Market.Spot, "api/v3/orderList/oco", BinanceLimit.Spot, weight: 1, method: HttpMethod.Post);

    private static readonly BinanceEndpoint SpotPairCancel =
        new(Market.Spot, "api/v3/orderList", BinanceLimit.Spot, weight: 1, method: HttpMethod.Delete);

    // An order of the Futures market that Binance takes as an order: at the market, or at a price.
    private static readonly BinanceEndpoint FuturesOrder =
        new(Market.Futures, "fapi/v1/order", BinanceLimit.Futures, weight: 0, method: HttpMethod.Post);

    private static readonly BinanceEndpoint FuturesOrderStatus = new(Market.Futures, "fapi/v1/order", BinanceLimit.Futures, weight: 1);

    private static readonly BinanceEndpoint FuturesOrderCancel =
        new(Market.Futures, "fapi/v1/order", BinanceLimit.Futures, weight: 1, method: HttpMethod.Delete);

    // An order of the Futures market that waits for a price: Binance takes a stop and a
    // take-profit there with the calls of its conditional orders only, and answers one at the
    // call above with -4120.
    private static readonly BinanceEndpoint FuturesStop =
        new(Market.Futures, "fapi/v1/algoOrder", BinanceLimit.Futures, weight: 0, "algoType=CONDITIONAL", method: HttpMethod.Post);

    private static readonly BinanceEndpoint FuturesStopStatus =
        new(Market.Futures, "fapi/v1/algoOrder", BinanceLimit.Futures, weight: 1);

    private static readonly BinanceEndpoint FuturesStopCancel =
        new(Market.Futures, "fapi/v1/algoOrder", BinanceLimit.Futures, weight: 1, method: HttpMethod.Delete);

    // The executions of one order of the Futures market.
    private static readonly BinanceEndpoint FuturesTrades = new(Market.Futures, "fapi/v1/userTrades", BinanceLimit.Futures, weight: 5);

    // How a symbol of the Futures market is margined in the account of a credential, which
    // Binance keeps for each symbol, and not for the account: with margin of its own for a
    // position, and at a leverage (FR-BIN-F2). Neither moves funds: the margin of a position is
    // taken from the wallet of the market when the position is opened, by the order.
    private static readonly BinanceEndpoint FuturesMarginType =
        new(Market.Futures, "fapi/v1/marginType", BinanceLimit.Futures, weight: 1, "marginType=ISOLATED", method: HttpMethod.Post);

    private static readonly BinanceEndpoint FuturesLeverage =
        new(Market.Futures, "fapi/v1/leverage", BinanceLimit.Futures, weight: 1, method: HttpMethod.Post);

    /// <summary>
    /// Begins the account event feed of the Futures market for the account of a credential
    /// (FR-BIN-7): Binance answers with a key that names the stream of the account's events,
    /// which is read at another address, and with the same key while one is valid. It takes the
    /// API key alone, with no signature and no parameter. It places nothing, and moves nothing.
    /// </summary>
    public static readonly BinanceEndpoint FuturesFeedStart =
        new(Market.Futures, "fapi/v1/listenKey", BinanceLimit.Futures, weight: 1, method: HttpMethod.Post);

    /// <summary>Renews that feed: Binance ends one that is not renewed within an hour. It takes the API key alone as well.</summary>
    public static readonly BinanceEndpoint FuturesFeedKeep =
        new(Market.Futures, "fapi/v1/listenKey", BinanceLimit.Futures, weight: 1, method: HttpMethod.Put);

    /// <summary>
    /// What the account event feed of the Spot market costs of the limit on requests when it is
    /// begun: the connection to Binance's WebSocket API, which Binance counts as 2, and the one
    /// request that is sent on it, which subscribes to the events of the account, as 2 more.
    /// It is no call of <see cref="BinanceCall"/>: <see cref="BinanceFeed"/> opens the
    /// connection, and asks the limiter for room with this.
    /// </summary>
    public static readonly BinanceEndpoint SpotFeed = new(Market.Spot, "ws-api/v3", BinanceLimit.Spot, weight: 4, reportsUsage: false);

    /// <summary>Every call, which is how the limiter knows the heaviest of each limit.</summary>
    public static readonly IReadOnlyList<BinanceEndpoint> All =
    [
        SpotTime, FuturesTime, KeyPermissions, SpotAccount, FuturesAccountSettings, FuturesBalances, FuturesPositions, FuturesFunding,
        SpotCandles, FuturesCandles, SpotPrice, FuturesPrice, SpotSymbols, FuturesSymbols, FuturesMarkPrice, FuturesOpenInterest,
        SpotOrder, SpotOrderStatus, SpotOrderCancel, SpotTrades, SpotPair, SpotPairCancel,
        FuturesOrder, FuturesOrderStatus, FuturesOrderCancel, FuturesStop, FuturesStopStatus, FuturesStopCancel, FuturesTrades,
        FuturesMarginType, FuturesLeverage, FuturesFeedStart, FuturesFeedKeep, SpotFeed,
    ];

    /// <summary>The clock of a market.</summary>
    public static BinanceEndpoint TimeOf(Market market) => market switch
    {
        Market.Spot => SpotTime,
        Market.Futures => FuturesTime,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
    };

    /// <summary>
    /// The newest candles of a symbol on a market, the one that is in progress last:
    /// <see cref="CandleRows"/> of them, whoever asks, so that one read serves everyone.
    /// </summary>
    public static BinanceEndpoint CandlesOf(Market market, string symbol, CandleInterval interval)
    {
        var candles = market switch
        {
            Market.Spot => SpotCandles,
            Market.Futures => FuturesCandles,
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

        // The length of a candle, by the name that Binance gives it.
        var length = interval switch
        {
            CandleInterval.FifteenMinutes => "15m",
            CandleInterval.OneHour => "1h",
            CandleInterval.FourHours => "4h",
            CandleInterval.OneDay => "1d",
            CandleInterval.OneWeek => "1w",
            _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
        };

        return candles.With(("symbol", symbol), ("interval", length), ("limit", CandleRows.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The price that a symbol last traded at on a market.</summary>
    public static BinanceEndpoint PriceOf(Market market, string symbol) => market switch
    {
        Market.Spot => SpotPrice.With(("symbol", symbol)),
        Market.Futures => FuturesPrice.With(("symbol", symbol)),
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
    };

    /// <summary>Every symbol of a market, with its rules.</summary>
    public static BinanceEndpoint SymbolsOf(Market market) => market switch
    {
        Market.Spot => SpotSymbols,
        Market.Futures => FuturesSymbols,
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
    };

    /// <summary>The mark price of a symbol of the Futures market, with its funding.</summary>
    public static BinanceEndpoint MarkPriceOf(string symbol) => FuturesMarkPrice.With(("symbol", symbol));

    /// <summary>The open interest of a symbol of the Futures market.</summary>
    public static BinanceEndpoint OpenInterestOf(string symbol) => FuturesOpenInterest.With(("symbol", symbol));

    /// <summary>The positions that the account of a credential holds in a symbol of the Futures market.</summary>
    public static BinanceEndpoint PositionsOf(string symbol) => FuturesPositions.With(("symbol", symbol));

    /// <summary>
    /// The funding that the account of a credential received and paid in a symbol of the Futures
    /// market between two moments, both of them included: <see cref="FundingRows"/> rows of it at
    /// most, which is the most that Binance gives in one answer.
    /// </summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="from">The first moment that is asked for.</param>
    /// <param name="to">The last moment that is asked for.</param>
    public static BinanceEndpoint FundingOf(string symbol, DateTimeOffset from, DateTimeOffset to) =>
        FuturesFunding.With(
            ("symbol", symbol),
            ("startTime", from.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            ("endTime", to.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),
            ("limit", FundingRows.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Has the positions of a symbol of the Futures market use margin of their own in the
    /// account of a credential: isolated margin (FR-BIN-F2). It is a setting of the symbol.
    /// </summary>
    public static BinanceEndpoint IsolatedMarginOf(string symbol) => FuturesMarginType.With(("symbol", symbol));

    /// <summary>Sets the leverage of a symbol of the Futures market in the account of a credential (FR-BIN-F2).</summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="leverage">The leverage, as a whole number.</param>
    public static BinanceEndpoint LeverageOf(string symbol, int leverage) =>
        FuturesLeverage.With(("symbol", symbol), ("leverage", leverage.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// An order that is placed at the market: it buys or sells a quantity at once, at the best
    /// prices that there are. It carries the identifier that its caller gave it (FR-BIN-6), and
    /// asks for an answer that says what became of it.
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="side">Binance's name of the side: <c>BUY</c> or <c>SELL</c>.</param>
    /// <param name="quantity">How much of the asset that is traded.</param>
    /// <param name="clientOrderId">The identifier of the order, which its caller chose.</param>
    /// <param name="closes">
    /// Whether the order only closes a position. On the Futures market such an order is sent as
    /// one that only reduces, so that it opens nothing when the position is gone already. The
    /// Spot market has no positions: a sale there sells what the account holds.
    /// </param>
    public static BinanceEndpoint MarketOrderOf(
        Market market, string symbol, string side, string quantity, string clientOrderId, bool closes) => market switch
        {
            // The full answer of the Spot market has the executions of the order, with their fees.
            Market.Spot => SpotOrder.With(
                ("symbol", symbol), ("side", side), ("type", "MARKET"), ("quantity", quantity),
                ("newClientOrderId", clientOrderId), ("newOrderRespType", "FULL")),
            Market.Futures => FuturesOrder.With(
            [
                ("symbol", symbol), ("side", side), ("type", "MARKET"), ("quantity", quantity),
                ("newClientOrderId", clientOrderId), ("newOrderRespType", "RESULT"),
                .. closes ? [("reduceOnly", "true")] : Array.Empty<(string, string)>(),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

    /// <summary>
    /// An order at a price: it is executed at that price or a better one, and what is not
    /// executed at once rests until it is, or until it is taken off the exchange (FR-BIN-S2).
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="side">Binance's name of the side: <c>BUY</c> or <c>SELL</c>.</param>
    /// <param name="quantity">How much of the asset that is traded.</param>
    /// <param name="price">The price of the order.</param>
    /// <param name="clientOrderId">The identifier of the order, which its caller chose.</param>
    /// <param name="closes">Whether the order only closes a position, as in <see cref="MarketOrderOf"/>.</param>
    public static BinanceEndpoint LimitOrderOf(
        Market market, string symbol, string side, string quantity, string price, string clientOrderId, bool closes) => market switch
        {
            // It may be executed at once, so the Spot market is asked for its executions, as for
            // an order at the market.
            Market.Spot => SpotOrder.With(
                ("symbol", symbol), ("side", side), ("type", "LIMIT"), ("timeInForce", "GTC"), ("quantity", quantity), ("price", price),
                ("newClientOrderId", clientOrderId), ("newOrderRespType", "FULL")),
            Market.Futures => FuturesOrder.With(
            [
                ("symbol", symbol), ("side", side), ("type", "LIMIT"), ("timeInForce", "GTC"), ("quantity", quantity), ("price", price),
                ("newClientOrderId", clientOrderId), ("newOrderRespType", "RESULT"),
                .. closes ? [("reduceOnly", "true")] : Array.Empty<(string, string)>(),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

    /// <summary>
    /// The pair of the Spot market that protects what a position holds: a take-profit above the
    /// price and a stop-loss below it, which both sell the same quantity, and each of which is
    /// placed at the market when the price reaches its trigger. Binance ends the other then
    /// (FR-BIN-S2). Each carries an identifier of its own, and the pair one more.
    /// </summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="quantity">How much of the asset that is traded each of the two sells.</param>
    /// <param name="pairId">The identifier of the pair, which its caller chose.</param>
    /// <param name="takeProfitPrice">The price that the take-profit is triggered at.</param>
    /// <param name="takeProfitId">The identifier of the take-profit.</param>
    /// <param name="stopLossPrice">The price that the stop-loss is triggered at.</param>
    /// <param name="stopLossId">The identifier of the stop-loss.</param>
    public static BinanceEndpoint PairOf(
        string symbol, string quantity, string pairId, string takeProfitPrice, string takeProfitId, string stopLossPrice, string stopLossId) =>
        SpotPair.With(
            ("symbol", symbol), ("side", "SELL"), ("quantity", quantity), ("listClientOrderId", pairId),
            ("aboveType", "TAKE_PROFIT"), ("aboveStopPrice", takeProfitPrice), ("aboveClientOrderId", takeProfitId),
            ("belowType", "STOP_LOSS"), ("belowStopPrice", stopLossPrice), ("belowClientOrderId", stopLossId),
            ("newOrderRespType", "RESULT"));

    /// <summary>Takes a pair of the Spot market off the exchange, by the identifier that its caller gave it.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="pairId">The identifier of the pair.</param>
    public static BinanceEndpoint PairCancelOf(string symbol, string pairId) =>
        SpotPairCancel.With(("symbol", symbol), ("listClientOrderId", pairId));

    /// <summary>
    /// An order that rests on the exchange until the price reaches its trigger, and is then
    /// placed at the market: a stop. On the Spot market it is a stop-loss order. On the Futures
    /// market it is a conditional order, which Binance takes with calls of its own, and it is
    /// triggered on the mark price (FR-BIN-F3).
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="side">Binance's name of the side: <c>BUY</c> or <c>SELL</c>.</param>
    /// <param name="quantity">How much of the asset that is traded.</param>
    /// <param name="triggerPrice">The price that the order is triggered at.</param>
    /// <param name="clientOrderId">The identifier of the order, which its caller chose.</param>
    /// <param name="closes">Whether the order only closes a position, as in <see cref="MarketOrderOf"/>.</param>
    public static BinanceEndpoint StopOrderOf(
        Market market, string symbol, string side, string quantity, string triggerPrice, string clientOrderId, bool closes) =>
        market switch
        {
            Market.Spot => SpotOrder.With(
                ("symbol", symbol), ("side", side), ("type", "STOP_LOSS"), ("quantity", quantity), ("stopPrice", triggerPrice),
                ("newClientOrderId", clientOrderId), ("newOrderRespType", "RESULT")),
            Market.Futures => FuturesStop.With(
            [
                ("symbol", symbol), ("side", side), ("type", "STOP_MARKET"), ("quantity", quantity), ("triggerPrice", triggerPrice),
                ("workingType", "MARK_PRICE"), ("clientAlgoId", clientOrderId),
                .. closes ? [("reduceOnly", "true")] : Array.Empty<(string, string)>(),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

    /// <summary>
    /// An order that rests on the exchange until the price reaches its trigger on the side of a
    /// profit, and is then placed at the market: a take-profit. A sale is triggered at its price
    /// or above, and a purchase at its price or below, where a stop is triggered the other way.
    /// On the Futures market it is a conditional order, as a stop is, and it is triggered on the
    /// mark price (FR-BIN-F3). On the Spot market the take-profit of a position is one order of
    /// a pair: see <see cref="PairOf"/>.
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="side">Binance's name of the side: <c>BUY</c> or <c>SELL</c>.</param>
    /// <param name="quantity">How much of the asset that is traded.</param>
    /// <param name="triggerPrice">The price that the order is triggered at.</param>
    /// <param name="clientOrderId">The identifier of the order, which its caller chose.</param>
    /// <param name="closes">Whether the order only closes a position, as in <see cref="MarketOrderOf"/>.</param>
    public static BinanceEndpoint TakeProfitOrderOf(
        Market market, string symbol, string side, string quantity, string triggerPrice, string clientOrderId, bool closes) =>
        market switch
        {
            Market.Spot => SpotOrder.With(
                ("symbol", symbol), ("side", side), ("type", "TAKE_PROFIT"), ("quantity", quantity), ("stopPrice", triggerPrice),
                ("newClientOrderId", clientOrderId), ("newOrderRespType", "RESULT")),
            Market.Futures => FuturesStop.With(
            [
                ("symbol", symbol), ("side", side), ("type", "TAKE_PROFIT_MARKET"), ("quantity", quantity), ("triggerPrice", triggerPrice),
                ("workingType", "MARK_PRICE"), ("clientAlgoId", clientOrderId),
                .. closes ? [("reduceOnly", "true")] : Array.Empty<(string, string)>(),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

    /// <summary>What became of an order, asked by the identifier that its caller gave it.</summary>
    /// <param name="market">The market.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="clientOrderId">The identifier of the order.</param>
    /// <param name="stop">Whether the order is a stop or a take-profit, which the Futures market keeps apart.</param>
    public static BinanceEndpoint OrderStatusOf(Market market, string symbol, string clientOrderId, bool stop) => market switch
    {
        Market.Spot => SpotOrderStatus.With(("symbol", symbol), ("origClientOrderId", clientOrderId)),
        Market.Futures when stop => FuturesStopStatus.With(("clientAlgoId", clientOrderId)),
        Market.Futures => FuturesOrderStatus.With(("symbol", symbol), ("origClientOrderId", clientOrderId)),
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
    };

    /// <summary>Takes an order off the exchange, by the identifier that its caller gave it.</summary>
    /// <param name="market">The market.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="clientOrderId">The identifier of the order.</param>
    /// <param name="stop">Whether the order is a stop or a take-profit, which the Futures market keeps apart.</param>
    public static BinanceEndpoint OrderCancelOf(Market market, string symbol, string clientOrderId, bool stop) => market switch
    {
        Market.Spot => SpotOrderCancel.With(("symbol", symbol), ("origClientOrderId", clientOrderId)),
        Market.Futures when stop => FuturesStopCancel.With(("clientAlgoId", clientOrderId)),
        Market.Futures => FuturesOrderCancel.With(("symbol", symbol), ("origClientOrderId", clientOrderId)),
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
    };

    /// <summary>The executions of one order, by the number that the exchange gave the order.</summary>
    /// <param name="market">The market.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="exchangeOrderId">The number that the exchange gave the order.</param>
    public static BinanceEndpoint TradesOf(Market market, string symbol, string exchangeOrderId) => market switch
    {
        Market.Spot => SpotTrades.With(("symbol", symbol), ("orderId", exchangeOrderId)),
        Market.Futures => FuturesTrades.With(("symbol", symbol), ("orderId", exchangeOrderId)),
        _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
    };

    private BinanceEndpoint(
        Market market, string path, BinanceLimit limit, int weight, string query = "", bool reportsUsage = true, HttpMethod? method = null)
    {
        Market = market;
        Path = path;
        Limit = limit;
        Weight = weight;
        Query = query;
        ReportsUsage = reportsUsage;
        Method = method ?? HttpMethod.Get;
    }

    /// <summary>
    /// The method of the call. It is <c>GET</c> for every call that reads. A call that places an
    /// order is a <c>POST</c>, and so is one that sets the margin of a symbol, and one that takes
    /// an order off the exchange is a <c>DELETE</c>.
    /// </summary>
    public HttpMethod Method { get; }

    /// <summary>
    /// Whether the call changes something at the exchange: it places an order, takes one off,
    /// sets the margin of a symbol, or begins or renews the account event feed.
    /// </summary>
    public bool Changes => Method != HttpMethod.Get;

    /// <summary>The market whose address of the configuration file serves the call.</summary>
    public Market Market { get; }

    /// <summary>The path of the call, under that address.</summary>
    public string Path { get; }

    /// <summary>The limit on requests that Binance counts the call against.</summary>
    public BinanceLimit Limit { get; }

    /// <summary>The request weight that Binance documents for the call: what one call costs of <see cref="Limit"/>.</summary>
    public int Weight { get; }

    /// <summary>
    /// The parameters that the call is made with, as they are sent, or nothing: those that it
    /// always has, and after them those of its caller.
    /// </summary>
    public string Query { get; }

    /// <summary>
    /// Whether the limiter goes by what an answer to the call says that Binance has counted
    /// against the address. It does, but for a call whose answer Binance says not to go by.
    /// </summary>
    public bool ReportsUsage { get; }

    /// <summary>
    /// This call with parameters of its caller, after those that it always has. A value is
    /// percent-encoded, so that whatever it is, it is one value of one parameter.
    /// </summary>
    private BinanceEndpoint With(params (string Name, string Value)[] parameters)
    {
        var given = string.Join('&', parameters.Select(parameter => $"{parameter.Name}={Uri.EscapeDataString(parameter.Value)}"));

        return new BinanceEndpoint(Market, Path, Limit, Weight, Query.Length == 0 ? given : $"{Query}&{given}", ReportsUsage, Method);
    }
}

/// <summary>
/// One limit on requests of Binance: the request weight that it allows an address in a minute,
/// whoever the calls are for and whichever key they carry (FR-BIN-4). Each is counted on its own,
/// and in each environment on its own. See docs/exchange.md.
/// </summary>
/// <remarks>
/// The numbers are what Binance documents, as the weights of the calls are. Its documentation
/// gives them as what <c>exchangeInfo</c> answers, which is where a change would show, and says
/// nothing of its test environment, which is taken to count as the real exchange does. What
/// Binance has counted comes back with every answer, in the header that the limit names, and
/// the limiter goes by it when it is more than its own count.
/// </remarks>
internal sealed class BinanceLimit
{
    private static readonly TradingMode[] Both = [TradingMode.Live, TradingMode.Demo];

    /// <summary>The API of the Spot market, under <c>api</c>.</summary>
    public static readonly BinanceLimit Spot = new("Spot", 6000, "X-MBX-USED-WEIGHT-1M", Both);

    /// <summary>The API of the Futures market, under <c>fapi</c>.</summary>
    public static readonly BinanceLimit Futures = new("Futures", 2400, "X-MBX-USED-WEIGHT-1M", Both);

    /// <summary>
    /// The permissions of a key. Binance's wallet API, under <c>sapi</c>, counts each of its
    /// calls on its own, and only the real exchange has it.
    /// </summary>
    public static readonly BinanceLimit KeyPermissions =
        new("Key permissions", 12000, "X-SAPI-USED-IP-WEIGHT-1M", [TradingMode.Live]);

    /// <summary>Every limit, in the order that their usage is reported in.</summary>
    public static readonly IReadOnlyList<BinanceLimit> All = [Spot, Futures, KeyPermissions];

    private BinanceLimit(string name, int weightPerMinute, string usageHeader, IReadOnlyList<TradingMode> environments)
    {
        Name = name;
        WeightPerMinute = weightPerMinute;
        UsageHeader = usageHeader;
        Environments = environments;
    }

    /// <summary>What the limit is on, in words for a status screen.</summary>
    public string Name { get; }

    /// <summary>The request weight that Binance allows an address in a minute.</summary>
    public int WeightPerMinute { get; }

    /// <summary>The header of an answer in which Binance says what it has counted against the address in its minute.</summary>
    public string UsageHeader { get; }

    /// <summary>The environments that have the calls of the limit.</summary>
    public IReadOnlyList<TradingMode> Environments { get; }
}
