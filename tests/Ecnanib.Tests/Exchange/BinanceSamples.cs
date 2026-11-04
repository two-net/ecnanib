namespace Ecnanib.Tests.Exchange;

/// <summary>Bodies shaped like Binance's, trimmed from real Demo responses.</summary>
internal static class BinanceSamples
{
    public static string SpotExchangeInfo(int weightLimit = 6000) => $$"""
        {"timezone":"UTC","serverTime":1793801870500,
         "rateLimits":[
           {"rateLimitType":"REQUEST_WEIGHT","interval":"MINUTE","intervalNum":1,"limit":{{weightLimit}}},
           {"rateLimitType":"ORDERS","interval":"SECOND","intervalNum":10,"limit":100},
           {"rateLimitType":"ORDERS","interval":"DAY","intervalNum":1,"limit":200000},
           {"rateLimitType":"RAW_REQUESTS","interval":"MINUTE","intervalNum":5,"limit":300000}],
         "exchangeFilters":[],
         "symbols":[{"symbol":"BTCUSDT","status":"TRADING","baseAsset":"BTC","baseAssetPrecision":8,"quoteAsset":"USDT",
           "orderTypes":["LIMIT","LIMIT_MAKER","MARKET","STOP_LOSS","STOP_LOSS_LIMIT","TAKE_PROFIT","TAKE_PROFIT_LIMIT"],
           "filters":[
             {"filterType":"PRICE_FILTER","minPrice":"0.01000000","maxPrice":"1000000.00000000","tickSize":"0.01000000"},
             {"filterType":"LOT_SIZE","minQty":"0.00001000","maxQty":"9000.00000000","stepSize":"0.00001000"},
             {"filterType":"ICEBERG_PARTS","limit":100},
             {"filterType":"MARKET_LOT_SIZE","minQty":"0.00000000","maxQty":"127.44699891","stepSize":"0.00000000"},
             {"filterType":"NOTIONAL","minNotional":"5.00000000","applyMinToMarket":true,"maxNotional":"9000000.00000000","applyMaxToMarket":false,"avgPriceMins":5},
             {"filterType":"MAX_NUM_ORDERS","maxNumOrders":200}]},
           {"symbol":"ETHBTC","status":"BREAK","baseAsset":"ETH","quoteAsset":"BTC","orderTypes":["LIMIT","MARKET"],"filters":[]}]}
        """;

    public const string FuturesExchangeInfo = """
        {"timezone":"UTC","serverTime":1793801870500,"futuresType":"U_MARGINED",
         "rateLimits":[
           {"rateLimitType":"REQUEST_WEIGHT","interval":"MINUTE","intervalNum":1,"limit":2400},
           {"rateLimitType":"ORDERS","interval":"MINUTE","intervalNum":1,"limit":1200},
           {"rateLimitType":"ORDERS","interval":"SECOND","intervalNum":10,"limit":300}],
         "exchangeFilters":[],"assets":[],
         "symbols":[{"symbol":"BTCUSDT","pair":"BTCUSDT","contractType":"PERPETUAL","status":"TRADING","baseAsset":"BTC","quoteAsset":"USDT",
           "marginAsset":"USDT","pricePrecision":2,"quantityPrecision":4,
           "orderTypes":["LIMIT","MARKET","STOP","STOP_MARKET","TAKE_PROFIT","TAKE_PROFIT_MARKET","TRAILING_STOP_MARKET"],
           "filters":[
             {"filterType":"PRICE_FILTER","minPrice":"261.10","tickSize":"0.10","maxPrice":"809484"},
             {"minQty":"0.0001","stepSize":"0.0001","filterType":"LOT_SIZE","maxQty":"1000"},
             {"minQty":"0.0001","maxQty":"120","stepSize":"0.0001","filterType":"MARKET_LOT_SIZE"},
             {"limit":10000,"filterType":"MAX_NUM_ORDERS"},
             {"filterType":"MIN_NOTIONAL","notional":"50"},
             {"filterType":"PERCENT_PRICE","multiplierUp":"1.0500","multiplierDown":"0.9500","multiplierDecimal":"4"}]}]}
        """;

    public const string SpotAccount = """
        {"makerCommission":10,"canTrade":true,"canWithdraw":false,"canDeposit":true,"accountType":"SPOT",
         "balances":[{"asset":"BTC","free":"0.00100000","locked":"0.00050000"},{"asset":"USDT","free":"250.12000000","locked":"0.00000000"}],
         "permissions":["SPOT"]}
        """;

    public const string FuturesAccount = """
        {"totalWalletBalance":"103.12345678","totalMarginBalance":"103.50000000","availableBalance":"103.12345678",
         "assets":[
           {"asset":"USDT","walletBalance":"103.12345678","marginBalance":"103.50000000","availableBalance":"103.12345678"},
           {"asset":"BNB","walletBalance":"0.00000000","marginBalance":"0.00000000","availableBalance":"0.00000000"}],
         "positions":[]}
        """;

    public static string SpotOrder(string clientOrderId, string status = "NEW", string type = "LIMIT") => $$"""
        {"symbol":"BTCUSDT","orderId":28,"orderListId":-1,"clientOrderId":"{{clientOrderId}}","transactTime":1507725176595,
         "price":"60000.00000000","origQty":"0.00200000","executedQty":"0.00100000","cummulativeQuoteQty":"60.50000000",
         "status":"{{status}}","timeInForce":"GTC","type":"{{type}}","side":"BUY","stopPrice":"0.00000000"}
        """;

    public static string FuturesOrder(string clientOrderId, string status = "FILLED", string type = "MARKET") => $$"""
        {"clientOrderId":"{{clientOrderId}}","cumQty":"0","cumQuote":"6050.00","executedQty":"0.100","orderId":22542179,
         "avgPrice":"60500.00","origQty":"0.100","price":"0","reduceOnly":false,"side":"SELL","positionSide":"BOTH",
         "status":"{{status}}","stopPrice":"0","closePosition":false,"symbol":"BTCUSDT","timeInForce":"GTC",
         "type":"{{type}}","origType":"{{type}}","updateTime":1566818724722,"workingType":"CONTRACT_PRICE","priceProtect":false}
        """;

    public static string AlgoOrder(string clientAlgoId, string status = "NEW", string actualOrderId = "") => $$"""
        {"algoId":2146760,"clientAlgoId":"{{clientAlgoId}}","algoType":"CONDITIONAL","orderType":"STOP_MARKET",
         "symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","timeInForce":"GTC","quantity":"0.100",
         "algoStatus":"{{status}}","actualOrderId":"{{actualOrderId}}","triggerPrice":"58000.00","price":"0.00",
         "workingType":"CONTRACT_PRICE","reduceOnly":true,"closePosition":false,"priceProtect":false,
         "createTime":1750485492076,"updateTime":1750485492076,"triggerTime":0}
        """;
}
