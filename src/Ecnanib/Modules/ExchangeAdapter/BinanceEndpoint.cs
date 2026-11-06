namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>
/// One Binance REST endpoint. The weight is reserved before a request is sent, so that concurrent
/// requests can't overshoot the budget together. It's an estimate, and never below 1, because Binance
/// charges a failed request even when a successful one is free. The response headers are what count.
/// </summary>
/// <param name="Path">Logged as the endpoint (FR-LOG-08). It never holds a parameter.</param>
/// <param name="Signed">Sent with the key, a timestamp, the receive window and a signature (EXT-BIN-03).</param>
/// <param name="Trade">
/// Places or cancels an order. It gets its own timeout and ignores the caller's token once sent, so a
/// shutdown can't cut it off (NFR-REL-04).
/// </param>
/// <param name="PlacesOrder">Counts against the key's order-rate limits (EXT-BIN-04).</param>
internal sealed record BinanceEndpoint(HttpMethod Method, string Path, int Weight, bool Signed, bool Trade = false, bool PlacesOrder = false)
{
    public static readonly BinanceEndpoint SpotTime = new(HttpMethod.Get, "/api/v3/time", 1, Signed: false);
    public static readonly BinanceEndpoint SpotExchangeInfo = new(HttpMethod.Get, "/api/v3/exchangeInfo", 20, Signed: false);
    public static readonly BinanceEndpoint SpotAccount = new(HttpMethod.Get, "/api/v3/account", 20, Signed: true);
    public static readonly BinanceEndpoint SpotNewOrder = new(HttpMethod.Post, "/api/v3/order", 1, Signed: true, Trade: true, PlacesOrder: true);
    public static readonly BinanceEndpoint SpotQueryOrder = new(HttpMethod.Get, "/api/v3/order", 4, Signed: true);
    public static readonly BinanceEndpoint SpotCancelOrder = new(HttpMethod.Delete, "/api/v3/order", 1, Signed: true, Trade: true);

    /// <summary>A one-cancels-the-other list. It places two orders, so the order count goes up by two.</summary>
    public static readonly BinanceEndpoint SpotNewOrderList = new(HttpMethod.Post, "/api/v3/orderList/oco", 1, Signed: true, Trade: true, PlacesOrder: true);

    public static readonly BinanceEndpoint SpotQueryOrderList = new(HttpMethod.Get, "/api/v3/orderList", 4, Signed: true);

    /// <summary>One symbol's open orders, order lists' members included. Binance charges 6 with a symbol, and 80 without.</summary>
    public static readonly BinanceEndpoint SpotOpenOrders = new(HttpMethod.Get, "/api/v3/openOrders", 6, Signed: true);
    public static readonly BinanceEndpoint SpotCancelOrderList = new(HttpMethod.Delete, "/api/v3/orderList", 1, Signed: true, Trade: true);

    /// <summary>One order's fills. Binance charges 5 when an order ID is given, and 20 otherwise.</summary>
    public static readonly BinanceEndpoint SpotMyTrades = new(HttpMethod.Get, "/api/v3/myTrades", 5, Signed: true);

    /// <summary>The account's fills in a symbol over a window of at most 24 hours, without an order ID, which Binance charges 20.</summary>
    public static readonly BinanceEndpoint SpotMyTradesByTime = new(HttpMethod.Get, "/api/v3/myTrades", 20, Signed: true);

    /// <summary>
    /// A key's permissions (FR-KEY-05), from the wallet API on the Spot Live server. Binance counts SAPI weight
    /// apart from /api weight and reports it in X-SAPI-USED-IP-WEIGHT-1M, so the response doesn't move the
    /// Spot Live budget, and reserving 1 from it is a slight overestimate.
    /// </summary>
    public static readonly BinanceEndpoint SpotApiRestrictions = new(HttpMethod.Get, "/sapi/v1/account/apiRestrictions", 1, Signed: true);

    /// <summary>Candles. Spot charges the same whatever the limit.</summary>
    public static readonly BinanceEndpoint SpotKlines = new(HttpMethod.Get, "/api/v3/klines", 2, Signed: false);

    /// <summary>One symbol's 24-hour statistics, which include the last price and the top of the book.</summary>
    public static readonly BinanceEndpoint SpotTicker24h = new(HttpMethod.Get, "/api/v3/ticker/24hr", 2, Signed: false);

    public static readonly BinanceEndpoint FuturesTime = new(HttpMethod.Get, "/fapi/v1/time", 1, Signed: false);
    public static readonly BinanceEndpoint FuturesExchangeInfo = new(HttpMethod.Get, "/fapi/v1/exchangeInfo", 1, Signed: false);
    public static readonly BinanceEndpoint FuturesAccount = new(HttpMethod.Get, "/fapi/v3/account", 5, Signed: true);
    public static readonly BinanceEndpoint FuturesNewOrder = new(HttpMethod.Post, "/fapi/v1/order", 1, Signed: true, Trade: true, PlacesOrder: true);
    public static readonly BinanceEndpoint FuturesQueryOrder = new(HttpMethod.Get, "/fapi/v1/order", 1, Signed: true);
    public static readonly BinanceEndpoint FuturesCancelOrder = new(HttpMethod.Delete, "/fapi/v1/order", 1, Signed: true, Trade: true);

    /// <summary>One symbol's open orders, conditional ones aside. Binance charges 1 with a symbol, and 40 without.</summary>
    public static readonly BinanceEndpoint FuturesOpenOrders = new(HttpMethod.Get, "/fapi/v1/openOrders", 1, Signed: true);

    /// <summary>One order's fills, or the account's in a symbol over a window of at most 7 days.</summary>
    public static readonly BinanceEndpoint FuturesUserTrades = new(HttpMethod.Get, "/fapi/v1/userTrades", 5, Signed: true);

    /// <summary>The account's income history, such as funding fees, a page of up to 1000 at a time.</summary>
    public static readonly BinanceEndpoint FuturesIncome = new(HttpMethod.Get, "/fapi/v1/income", 30, Signed: true);

    /// <summary>Conditional orders, since Binance moved them to its algo order service on 2025-12-09.</summary>
    public static readonly BinanceEndpoint FuturesNewAlgoOrder = new(HttpMethod.Post, "/fapi/v1/algoOrder", 1, Signed: true, Trade: true, PlacesOrder: true);
    public static readonly BinanceEndpoint FuturesQueryAlgoOrder = new(HttpMethod.Get, "/fapi/v1/algoOrder", 1, Signed: true);
    public static readonly BinanceEndpoint FuturesCancelAlgoOrder = new(HttpMethod.Delete, "/fapi/v1/algoOrder", 1, Signed: true, Trade: true);

    /// <summary>One symbol's open conditional orders. Binance charges 1 with a symbol, and 40 without.</summary>
    public static readonly BinanceEndpoint FuturesOpenAlgoOrders = new(HttpMethod.Get, "/fapi/v1/openAlgoOrders", 1, Signed: true);

    /// <summary>One symbol's best bid and ask.</summary>
    public static readonly BinanceEndpoint FuturesBookTicker = new(HttpMethod.Get, "/fapi/v1/ticker/bookTicker", 2, Signed: false);

    /// <summary>One symbol's last price.</summary>
    public static readonly BinanceEndpoint FuturesPrice = new(HttpMethod.Get, "/fapi/v1/ticker/price", 1, Signed: false);

    /// <summary>One symbol's mark price, index price and funding.</summary>
    public static readonly BinanceEndpoint FuturesPremiumIndex = new(HttpMethod.Get, "/fapi/v1/premiumIndex", 1, Signed: false);

    /// <summary>One symbol's positions, with their liquidation prices.</summary>
    public static readonly BinanceEndpoint FuturesPositionRisk = new(HttpMethod.Get, "/fapi/v3/positionRisk", 5, Signed: true);

    /// <summary>The account's position mode, Multi-Assets mode and whether it may trade (EXT-BIN-07).</summary>
    public static readonly BinanceEndpoint FuturesAccountConfig = new(HttpMethod.Get, "/fapi/v1/accountConfig", 5, Signed: true);

    /// <summary>A symbol's margin type and leverage on the account (FR-RISK-09).</summary>
    public static readonly BinanceEndpoint FuturesSymbolConfig = new(HttpMethod.Get, "/fapi/v1/symbolConfig", 5, Signed: true);

    /// <summary>A symbol's notional and leverage brackets, which can differ per account.</summary>
    public static readonly BinanceEndpoint FuturesLeverageBracket = new(HttpMethod.Get, "/fapi/v1/leverageBracket", 1, Signed: true);

    /// <summary>
    /// The symbols whose funding interval, cap or floor Binance adjusted. Binance gives it weight 0, under a
    /// limit of its own of 500 per 5 minutes shared with the funding history, so reserving 1 is an overestimate.
    /// </summary>
    public static readonly BinanceEndpoint FuturesFundingInfo = new(HttpMethod.Get, "/fapi/v1/fundingInfo", 1, Signed: false);

    /// <summary>A symbol's funding rate history. Like the funding information, it's under the limit of 500 per 5 minutes, so 1 is reserved.</summary>
    public static readonly BinanceEndpoint FuturesFundingRate = new(HttpMethod.Get, "/fapi/v1/fundingRate", 1, Signed: false);

    /// <summary>
    /// Changes a symbol's margin type. It places no order and is safe to send again, so it isn't a trade:
    /// it keeps the caller's token and the read timeout.
    /// </summary>
    public static readonly BinanceEndpoint FuturesMarginType = new(HttpMethod.Post, "/fapi/v1/marginType", 1, Signed: true);

    /// <summary>Changes a symbol's leverage. Like the margin type, it isn't a trade.</summary>
    public static readonly BinanceEndpoint FuturesLeverage = new(HttpMethod.Post, "/fapi/v1/leverage", 1, Signed: true);

    private static readonly BinanceEndpoint[] FuturesKlinesByWeight =
    [
        new(HttpMethod.Get, "/fapi/v1/klines", 1, Signed: false),
        new(HttpMethod.Get, "/fapi/v1/klines", 2, Signed: false),
        new(HttpMethod.Get, "/fapi/v1/klines", 5, Signed: false),
        new(HttpMethod.Get, "/fapi/v1/klines", 10, Signed: false),
    ];

    /// <summary>Candles. Futures charges by the limit: 1 below 100, 2 below 500, 5 up to 1000, and 10 above.</summary>
    public static BinanceEndpoint FuturesKlines(int limit) => limit switch
    {
        < 100 => FuturesKlinesByWeight[0],
        < 500 => FuturesKlinesByWeight[1],
        <= 1000 => FuturesKlinesByWeight[2],
        _ => FuturesKlinesByWeight[3],
    };
}
