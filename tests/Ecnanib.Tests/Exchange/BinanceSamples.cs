using System.Globalization;

namespace Ecnanib.Tests.Exchange;

/// <summary>Bodies shaped like Binance's, trimmed from real Demo responses.</summary>
internal static class BinanceSamples
{
    /// <summary>Binance's weeks start on Monday. The Unix epoch was a Thursday.</summary>
    private static readonly DateTimeOffset FirstMonday = new(1970, 1, 5, 0, 0, 0, TimeSpan.Zero);

    public static TimeSpan IntervalLength(string interval) => interval switch
    {
        "15m" => TimeSpan.FromMinutes(15),
        "1h" => TimeSpan.FromHours(1),
        "4h" => TimeSpan.FromHours(4),
        "1d" => TimeSpan.FromDays(1),
        "1w" => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
    };

    /// <summary>The open time of the candle that contains <paramref name="at"/>, aligned as Binance aligns it.</summary>
    public static DateTimeOffset Floor(string interval, DateTimeOffset at)
    {
        var anchor = interval == "1w" ? FirstMonday : DateTimeOffset.UnixEpoch;
        var length = IntervalLength(interval).Ticks;

        return new DateTimeOffset(at.UtcTicks - ((at.UtcTicks - anchor.UtcTicks) % length), TimeSpan.Zero);
    }

    /// <summary>
    /// Answers GET klines as Binance does: candles whose open times are between startTime and endTime,
    /// both included, the first <c>limit</c> of them from startTime, or the last <c>limit</c> up to endTime
    /// without one. That includes the candle still open at <paramref name="now"/> when the range reaches
    /// it. A candle <paramref name="missing"/> picks out isn't there, as during maintenance.
    /// </summary>
    public static string Klines(RecordedRequest request, DateTimeOffset now, Func<DateTimeOffset, bool>? missing = null)
    {
        var interval = request.Parameter("interval")!;
        var length = IntervalLength(interval);
        var limit = int.Parse(request.Parameter("limit") ?? "500", CultureInfo.InvariantCulture);
        var end = Time(request.Parameter("endTime")) ?? now;
        var last = Floor(interval, end < now ? end : now);
        var opens = new List<DateTimeOffset>();

        if (Time(request.Parameter("startTime")) is { } start)
        {
            var first = Floor(interval, start) < start ? Floor(interval, start) + length : start;

            for (var open = first; open <= last && opens.Count < limit; open += length)
            {
                opens.Add(open);
            }
        }
        else
        {
            for (var open = last; opens.Count < limit; open -= length)
            {
                opens.Insert(0, open);
            }
        }

        return "[" + string.Join(",", opens.Where(open => missing?.Invoke(open) != true).Select(open => Kline(open, length))) + "]";
    }

    /// <summary>One candle, with prices that follow from its open time.</summary>
    public static string Kline(DateTimeOffset open, TimeSpan length)
    {
        var step = open.ToUnixTimeSeconds() / (long)length.TotalSeconds;
        var basePrice = 100m + (step % 40);
        var volume = 10m + (step % 7);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{open.ToUnixTimeMilliseconds()},\"{basePrice:0.00000000}\",\"{basePrice + 2:0.00000000}\",\"{basePrice - 1:0.00000000}\",\"{basePrice + 0.5m:0.00000000}\","
            + $"\"{volume:0.00000000}\",{(open + length).ToUnixTimeMilliseconds() - 1},\"{volume * basePrice:0.00000000}\",100,\"1.00000000\",\"100.00000000\",\"0\"]");
    }

    /// <summary>GET /api/v3/ticker/24hr for one symbol, in Binance's FULL form.</summary>
    public const string SpotTicker24h = """
        {"symbol":"BTCUSDT","priceChange":"-94.99999800","priceChangePercent":"-95.960","weightedAvgPrice":"0.29628482",
         "prevClosePrice":"0.10002000","lastPrice":"60123.45000000","lastQty":"200.00000000","bidPrice":"60123.44000000",
         "bidQty":"1.25000000","askPrice":"60123.46000000","askQty":"0.75000000","openPrice":"99.00000000",
         "highPrice":"100.00000000","lowPrice":"0.10000000","volume":"8913.30000000","quoteVolume":"15.30000000",
         "openTime":1499783499040,"closeTime":1499869899040,"firstId":28385,"lastId":28460,"count":76}
        """;

    public const string FuturesBookTicker = """
        {"symbol":"BTCUSDT","bidPrice":"60120.10","bidQty":"3.512","askPrice":"60120.20","askQty":"0.041","time":1589437530011,"lastUpdateId":1027024}
        """;

    public const string FuturesPrice = """
        {"symbol":"BTCUSDT","price":"60120.10","time":1589437530011}
        """;

    /// <summary>GET /fapi/v1/premiumIndex for one symbol. Without funding, as for a delivery contract, the rate is empty and the time is 0.</summary>
    public static string PremiumIndex(bool funding = true) => $$"""
        {"symbol":"BTCUSDT","markPrice":"60118.75000000","indexPrice":"60131.02340000","estimatedSettlePrice":"60129.11000000",
         "lastFundingRate":"{{(funding ? "0.00010000" : "")}}","interestRate":"0.00010000",
         "nextFundingTime":{{(funding ? "1793808000000" : "0")}},"time":1793804395002}
        """;

    /// <summary>GET /fapi/v3/positionRisk for BTCUSDT: a short, and an entry with only open orders, which has no amount.</summary>
    public const string PositionRisk = """
        [{"symbol":"BTCUSDT","positionSide":"BOTH","positionAmt":"-0.010","entryPrice":"60500.0","breakEvenPrice":"60530.25",
          "markPrice":"60123.4","unRealizedProfit":"3.76600000","liquidationPrice":"66012.3","isolatedMargin":"60.50000000",
          "notional":"-601.234","marginAsset":"USDT","isolatedWallet":"60.5","initialMargin":"120.2","maintMargin":"2.4",
          "positionInitialMargin":"120.2","openOrderInitialMargin":"0","adl":1,"bidNotional":"0","askNotional":"0",
          "updateTime":1720736417660},
         {"symbol":"BTCUSDT","positionSide":"LONG","positionAmt":"0.000","entryPrice":"0.0","breakEvenPrice":"0.0",
          "markPrice":"60123.4","unRealizedProfit":"0.00000000","liquidationPrice":"0","isolatedMargin":"0",
          "notional":"0","marginAsset":"USDT","isolatedWallet":"0","initialMargin":"0","maintMargin":"0",
          "positionInitialMargin":"0","openOrderInitialMargin":"12.1","adl":0,"bidNotional":"121","askNotional":"0",
          "updateTime":0}]
        """;

    private static DateTimeOffset? Time(string? milliseconds) =>
        milliseconds is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(milliseconds, CultureInfo.InvariantCulture));

    /// <summary>
    /// GET /api/v3/exchangeInfo with BTCUSDT and, unless a test delists it, ETHBTC. BTCUSDT bounds prices by side,
    /// as Binance's does now, or with the older filter for both sides when <paramref name="legacyPercentPrice"/> is set.
    /// </summary>
    public static string SpotExchangeInfo(
        int weightLimit = 6000, string btcTickSize = "0.01000000", bool ethBtc = true, bool legacyPercentPrice = false) => $$"""
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
             {"filterType":"PRICE_FILTER","minPrice":"0.01000000","maxPrice":"1000000.00000000","tickSize":"{{btcTickSize}}"},
             {"filterType":"LOT_SIZE","minQty":"0.00001000","maxQty":"9000.00000000","stepSize":"0.00001000"},
             {"filterType":"ICEBERG_PARTS","limit":100},
             {"filterType":"MARKET_LOT_SIZE","minQty":"0.00000000","maxQty":"127.44699891","stepSize":"0.00000000"},
             {"filterType":"NOTIONAL","minNotional":"5.00000000","applyMinToMarket":true,"maxNotional":"9000000.00000000","applyMaxToMarket":false,"avgPriceMins":5},
             {{(legacyPercentPrice ? SpotPercentPrice : SpotPercentPriceBySide)}},
             {"filterType":"MAX_NUM_ORDERS","maxNumOrders":200}]}{{(ethBtc ? EthBtc : "")}}]}
        """;

    private const string SpotPercentPriceBySide = """
        {"filterType":"PERCENT_PRICE_BY_SIDE","bidMultiplierUp":"5","bidMultiplierDown":"0.2","askMultiplierUp":"5","askMultiplierDown":"0.2","avgPriceMins":5}
        """;

    private const string SpotPercentPrice = """
        {"filterType":"PERCENT_PRICE","multiplierUp":"1.3","multiplierDown":"0.7","avgPriceMins":5}
        """;

    private const string EthBtc = """
        ,{"symbol":"ETHBTC","status":"BREAK","baseAsset":"ETH","quoteAsset":"BTC","orderTypes":["LIMIT","MARKET"],"filters":[]}
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
         "permissions":["SPOT"],"uid":354937868}
        """;

    /// <summary>A Spot account, shaped like <see cref="SpotAccount"/>, for a chosen account and trading permission.</summary>
    public static string SpotAccountOf(long uid, bool canTrade = true) => $$"""
        {"makerCommission":10,"canTrade":{{(canTrade ? "true" : "false")}},"canWithdraw":false,"canDeposit":true,"accountType":"SPOT",
         "balances":[],"permissions":["SPOT"],"uid":{{uid}}}
        """;

    /// <summary>GET /sapi/v1/account/apiRestrictions, from Binance's documented example.</summary>
    public static string ApiRestrictions(
        bool ipRestrict = true,
        bool withdrawals = false,
        bool spot = true,
        bool futures = true) => $$"""
        {"ipRestrict":{{Json(ipRestrict)}},"createTime":1698645219000,"enableReading":true,"enableWithdrawals":{{Json(withdrawals)}},
         "enableInternalTransfer":false,"enableMargin":false,"enableFutures":{{Json(futures)}},"permitsUniversalTransfer":false,
         "enableVanillaOptions":false,"enableFixApiTrade":false,"enableFixReadOnly":false,
         "enableSpotAndMarginTrading":{{Json(spot)}},"enablePortfolioMarginTrading":false}
        """;

    private static string Json(bool value) => value ? "true" : "false";

    public const string FuturesAccount = """
        {"totalWalletBalance":"103.12345678","totalMarginBalance":"103.50000000","availableBalance":"103.12345678",
         "assets":[
           {"asset":"USDT","walletBalance":"103.12345678","marginBalance":"103.50000000","availableBalance":"103.12345678"},
           {"asset":"BNB","walletBalance":"0.00000000","marginBalance":"0.00000000","availableBalance":"0.00000000"}],
         "positions":[]}
        """;

    /// <summary>A Futures account's settings: One-way and Single-Asset Mode unless a test says otherwise.</summary>
    public static string FuturesAccountConfig(bool canTrade = true, bool hedgeMode = false, bool multiAssets = false) => $$"""
        {"feeTier":0,"canTrade":{{Json(canTrade)}},"canDeposit":true,"canWithdraw":true,
         "dualSidePosition":{{Json(hedgeMode)}},"updateTime":0,"multiAssetsMargin":{{Json(multiAssets)}},"tradeGroupId":-1}
        """;

    /// <summary>One symbol's settings, as an array, the way Binance documents it.</summary>
    public static string SymbolConfig(string marginType = "CROSSED", int leverage = 20, string symbol = "BTCUSDT") => $$"""
        [{"symbol":"{{symbol}}","marginType":"{{marginType}}","isAutoAddMargin":"false","leverage":{{leverage}},"maxNotionalValue":"80000000"}]
        """;

    /// <summary>
    /// BTCUSDT's first three brackets, from Binance's documented shape, as an array of symbols or as the one symbol.
    /// At leverage 2 a position up to 500,000 USDT is in the first.
    /// </summary>
    public static string LeverageBrackets(bool single = false, string symbol = "BTCUSDT")
    {
        var entry = $$"""
            {"symbol":"{{symbol}}","brackets":[
              {"bracket":2,"initialLeverage":100,"notionalCap":600000,"notionalFloor":500000,"maintMarginRatio":0.005,"cum":750.0},
              {"bracket":1,"initialLeverage":125,"notionalCap":500000,"notionalFloor":0,"maintMarginRatio":0.004,"cum":0.0},
              {"bracket":3,"initialLeverage":75,"notionalCap":3000000,"notionalFloor":600000,"maintMarginRatio":0.0065,"cum":1650.0}]}
            """;

        return single ? entry : $"[{entry}]";
    }

    /// <summary>
    /// The symbols whose funding Binance adjusted: ETHUSDT every 4 hours, and one listed for its cap alone. BTCUSDT
    /// isn't listed, so it's on the default 8 hours.
    /// </summary>
    public const string FundingInfo = """
        [{"symbol":"ETHUSDT","adjustedFundingRateCap":"0.02500000","adjustedFundingRateFloor":"-0.02500000","fundingIntervalHours":4,"disclaimer":false},
         {"symbol":"XRPUSDT","adjustedFundingRateCap":"0.03000000","adjustedFundingRateFloor":"-0.03000000","fundingIntervalHours":0,"disclaimer":false}]
        """;

    public static string Leverage(int leverage, string symbol = "BTCUSDT") => $$"""
        {"leverage":{{leverage}},"maxNotionalValue":"1000000","symbol":"{{symbol}}"}
        """;

    public const string MarginTypeChanged = """
        {"code":200,"msg":"success"}
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

    public static string OrderList(string listClientOrderId, string targetClientOrderId, string stopClientOrderId, string status = "EXECUTING") => $$"""
        {"orderListId":1929,"contingencyType":"OCO","listStatusType":"EXEC_STARTED","listOrderStatus":"{{status}}",
         "listClientOrderId":"{{listClientOrderId}}","transactionTime":1711062760648,"symbol":"BTCUSDT",
         "orders":[{"symbol":"BTCUSDT","orderId":31,"clientOrderId":"{{targetClientOrderId}}"},
                   {"symbol":"BTCUSDT","orderId":32,"clientOrderId":"{{stopClientOrderId}}"}],
         "orderReports":[]}
        """;

    /// <summary>Spot's myTrades for order 28, in two fills, each with its commission in the base asset.</summary>
    public const string SpotTrades = """
        [{"symbol":"BTCUSDT","id":28457,"orderId":28,"orderListId":-1,"price":"60000.00","qty":"0.0006","quoteQty":"36.00",
          "commission":"0.0000006","commissionAsset":"BTC","time":1499865549590,"isBuyer":true,"isMaker":false,"isBestMatch":true},
         {"symbol":"BTCUSDT","id":28458,"orderId":28,"orderListId":-1,"price":"60010.00","qty":"0.0004","quoteQty":"24.004",
          "commission":"0.0000004","commissionAsset":"BTC","time":1499865549591,"isBuyer":true,"isMaker":true,"isBestMatch":true}]
        """;

    /// <summary>Futures' income history: two funding payments in BTCUSDT, one paid and one received, and a commission.</summary>
    public const string FundingIncome = """
        [{"symbol":"BTCUSDT","incomeType":"FUNDING_FEE","income":"-0.37500000","asset":"USDT","info":"FUNDING_FEE","time":1570608000000,"tranId":9689322392,"tradeId":""},
         {"symbol":"BTCUSDT","incomeType":"COMMISSION","income":"-0.01000000","asset":"USDT","info":"COMMISSION","time":1570610000000,"tranId":9689322393,"tradeId":"2059192"},
         {"symbol":"BTCUSDT","incomeType":"FUNDING_FEE","income":"0.12500000","asset":"USDT","info":"FUNDING_FEE","time":1570636800000,"tranId":9689322394,"tradeId":""}]
        """;

    /// <summary>Futures' funding rate history for BTCUSDT.</summary>
    public const string FundingRates = """
        [{"symbol":"BTCUSDT","fundingRate":"-0.03750000","fundingTime":1570608000000,"markPrice":"34287.54619963"},
         {"symbol":"BTCUSDT","fundingRate":"0.00010000","fundingTime":1570636800000,"markPrice":"34651.40000000"}]
        """;

    /// <summary>Futures' userTrades for order 22542179, with its commission in the margin asset.</summary>
    public const string FuturesTrades = """
        [{"buyer":false,"commission":"2.42","commissionAsset":"USDT","id":698759,"maker":false,"orderId":22542179,"price":"60500.00",
          "qty":"0.100","quoteQty":"6050.00","realizedPnl":"0","side":"SELL","positionSide":"BOTH","symbol":"BTCUSDT","time":1569514978020}]
        """;
}
