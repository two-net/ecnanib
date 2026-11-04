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
}
