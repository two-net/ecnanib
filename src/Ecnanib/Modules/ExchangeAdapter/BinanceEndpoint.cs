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

    /// <summary>Conditional orders, since Binance moved them to its algo order service on 2025-12-09.</summary>
    public static readonly BinanceEndpoint FuturesNewAlgoOrder = new(HttpMethod.Post, "/fapi/v1/algoOrder", 1, Signed: true, Trade: true, PlacesOrder: true);
    public static readonly BinanceEndpoint FuturesQueryAlgoOrder = new(HttpMethod.Get, "/fapi/v1/algoOrder", 1, Signed: true);
    public static readonly BinanceEndpoint FuturesCancelAlgoOrder = new(HttpMethod.Delete, "/fapi/v1/algoOrder", 1, Signed: true, Trade: true);

    /// <summary>One symbol's best bid and ask.</summary>
    public static readonly BinanceEndpoint FuturesBookTicker = new(HttpMethod.Get, "/fapi/v1/ticker/bookTicker", 2, Signed: false);

    /// <summary>One symbol's last price.</summary>
    public static readonly BinanceEndpoint FuturesPrice = new(HttpMethod.Get, "/fapi/v1/ticker/price", 1, Signed: false);

    /// <summary>One symbol's mark price, index price and funding.</summary>
    public static readonly BinanceEndpoint FuturesPremiumIndex = new(HttpMethod.Get, "/fapi/v1/premiumIndex", 1, Signed: false);

    /// <summary>One symbol's positions, with their liquidation prices.</summary>
    public static readonly BinanceEndpoint FuturesPositionRisk = new(HttpMethod.Get, "/fapi/v3/positionRisk", 5, Signed: true);

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
