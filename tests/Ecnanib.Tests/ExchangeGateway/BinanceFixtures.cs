using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Ecnanib.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.ExchangeGateway;

/// <summary>
/// What the tests of the gateway send to Binance, and what Binance answers each call with, in
/// the form that its documentation gives. No call leaves the tests: a <see cref="StandInSystem"/>
/// answers with these.
/// </summary>
internal static class BinanceCalls
{
    /// <summary>The area of the log of outbound calls, which has every call that a client of the host makes.</summary>
    public const string HttpArea = "Ecnanib.Platform.Adapters.Http.OutboundCallLogger";

    /// <summary>The area of what the gateway itself says about a call.</summary>
    public const string GatewayArea = "Ecnanib.ExchangeGateway.Adapters.Binance.BinanceCall";

    /// <summary>The answer of a clock.</summary>
    public const string Time = """{ "serverTime": 1794562200250 }""";

    /// <summary>The account of the Spot market, with what it holds of two assets.</summary>
    public const string SpotAccount =
        """
        {
          "makerCommission": 15, "takerCommission": 15, "buyerCommission": 0, "sellerCommission": 0,
          "commissionRates": { "maker": "0.00150000", "taker": "0.00150000", "buyer": "0.00000000", "seller": "0.00000000" },
          "canTrade": true, "canWithdraw": true, "canDeposit": true, "brokered": false, "requireSelfTradePrevention": false,
          "preventSor": false, "updateTime": 123456789, "accountType": "SPOT",
          "balances": [
            { "asset": "BTC", "free": "4723846.89208129", "locked": "0.00000000" },
            { "asset": "USDT", "free": "1250.50000000", "locked": "249.50000001" }
          ],
          "permissions": [ "SPOT" ], "uid": 354937868
        }
        """;

    /// <summary>The wallet of the Futures market: an asset it holds, one it holds none of, and one it owes.</summary>
    public const string FuturesBalances =
        """
        [
          { "accountAlias": "SgsR", "asset": "USDT", "balance": "122607.35137903", "crossWalletBalance": "23.72469206",
            "crossUnPnl": "0.00000000", "availableBalance": "23.72469206", "maxWithdrawAmount": "23.72469206",
            "marginAvailable": true, "updateTime": 1617939110373 },
          { "accountAlias": "SgsR", "asset": "BNB", "balance": "0.00000000", "crossWalletBalance": "0.00000000",
            "crossUnPnl": "0.00000000", "availableBalance": "0.00000000", "maxWithdrawAmount": "0.00000000",
            "marginAvailable": true, "updateTime": 0 },
          { "accountAlias": "SgsR", "asset": "USDC", "balance": "-1.25000000", "crossWalletBalance": "-1.25000000",
            "crossUnPnl": "0.00000000", "availableBalance": "0.00000000", "maxWithdrawAmount": "0.00000000",
            "marginAvailable": true, "updateTime": 1617939110373 }
        ]
        """;

    /// <summary>What the real exchange says of a key as a careful user makes it.</summary>
    public const string Restrictions =
        """
        { "ipRestrict": true, "createTime": 1623840271000, "enableReading": true, "enableWithdrawals": false,
          "enableInternalTransfer": false, "enableMargin": false, "enableFutures": true, "permitsUniversalTransfer": false,
          "enableVanillaOptions": false, "enableSpotAndMarginTrading": true, "enablePortfolioMarginTrading": false }
        """;

    /// <summary>The settings of the account of the Futures market.</summary>
    public const string FuturesAccountSettings =
        """{ "feeTier": 0, "canTrade": true, "canDeposit": true, "canWithdraw": true, "dualSidePosition": false, "updateTime": 0 }""";

    /// <summary>The price that a symbol last traded at on the Spot market.</summary>
    public const string SpotPrice = """{ "symbol": "BTCUSDT", "price": "104250.01000000" }""";

    /// <summary>The same on the Futures market, which says when as well.</summary>
    public const string FuturesPrice = """{ "symbol": "BTCUSDT", "price": "104251.70", "time": 1794735000113 }""";

    /// <summary>The mark price of a symbol of the Futures market, with a funding rate at which shorts pay longs.</summary>
    public const string MarkPrice =
        """
        { "symbol": "BTCUSDT", "markPrice": "104250.64871234", "indexPrice": "104262.13871795",
          "estimatedSettlePrice": "104255.08400000", "lastFundingRate": "-0.00003000", "interestRate": "0.00010000",
          "nextFundingTime": 1794758400000, "time": 1794735000003 }
        """;

    /// <summary>The open interest of a symbol of the Futures market.</summary>
    public const string OpenInterest = """{ "openInterest": "10659.509", "symbol": "BTCUSDT", "time": 1794735000011 }""";

    /// <summary>
    /// Every symbol of the Spot market with its rules: one that the configuration file of the
    /// tests does not list, and its two, in another order than the file's. One names the least
    /// value of an order as Binance used to, is not traded for the moment, and allows the pair
    /// of a stop-loss and a take-profit without taking either of the two.
    /// </summary>
    public const string SpotSymbols =
        """
        {
          "timezone": "UTC", "serverTime": 1794735000250,
          "rateLimits": [ { "rateLimitType": "REQUEST_WEIGHT", "interval": "MINUTE", "intervalNum": 1, "limit": 6000 } ],
          "exchangeFilters": [],
          "symbols": [
            { "symbol": "ETHBTC", "status": "TRADING", "baseAsset": "ETH", "baseAssetPrecision": 8, "quoteAsset": "BTC",
              "quoteAssetPrecision": 8, "orderTypes": [ "LIMIT", "MARKET" ], "isSpotTradingAllowed": true,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.00001000", "maxPrice": "922327.00000000", "tickSize": "0.00001000" },
                { "filterType": "LOT_SIZE", "minQty": "0.00010000", "maxQty": "100000.00000000", "stepSize": "0.00010000" },
                { "filterType": "NOTIONAL", "minNotional": "0.00010000", "applyMinToMarket": true, "maxNotional": "9000000.00000000",
                  "applyMaxToMarket": false, "avgPriceMins": 5 }
              ], "permissions": [], "permissionSets": [] },
            { "symbol": "SOLUSDT", "status": "BREAK", "baseAsset": "SOL", "baseAssetPrecision": 8, "quoteAsset": "USDT",
              "quoteAssetPrecision": 8, "orderTypes": [ "LIMIT", "MARKET" ], "ocoAllowed": true, "isSpotTradingAllowed": true,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.01000000", "maxPrice": "100000.00000000", "tickSize": "0.01000000" },
                { "filterType": "LOT_SIZE", "minQty": "0.00100000", "maxQty": "90000.00000000", "stepSize": "0.00100000" },
                { "filterType": "MIN_NOTIONAL", "minNotional": "5.00000000", "applyToMarket": true, "avgPriceMins": 5 }
              ], "permissions": [], "permissionSets": [] },
            { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "baseAssetPrecision": 8, "quoteAsset": "USDT",
              "quoteAssetPrecision": 8,
              "orderTypes": [ "LIMIT", "LIMIT_MAKER", "MARKET", "STOP_LOSS", "STOP_LOSS_LIMIT", "TAKE_PROFIT", "TAKE_PROFIT_LIMIT" ],
              "ocoAllowed": true, "otoAllowed": true, "isSpotTradingAllowed": true,
              "filters": [
                { "filterType": "PRICE_FILTER", "minPrice": "0.01000000", "maxPrice": "1000000.00000000", "tickSize": "0.01000000" },
                { "filterType": "LOT_SIZE", "minQty": "0.00001000", "maxQty": "9000.00000000", "stepSize": "0.00001000" },
                { "filterType": "ICEBERG_PARTS", "limit": 10 },
                { "filterType": "MARKET_LOT_SIZE", "minQty": "0.00000000", "maxQty": "107.94730146", "stepSize": "0.00000000" },
                { "filterType": "NOTIONAL", "minNotional": "5.00000000", "applyMinToMarket": true, "maxNotional": "9000000.00000000",
                  "applyMaxToMarket": false, "avgPriceMins": 5 }
              ], "permissions": [], "permissionSets": [] }
          ]
        }
        """;

    /// <summary>
    /// Every symbol of the Futures market with its rules: of the two of the configuration file
    /// of the tests it has one, and a contract of the same asset that is delivered, which is
    /// another symbol. It names the least value of an order in its own way.
    /// </summary>
    public const string FuturesSymbols =
        """
        {
          "exchangeFilters": [],
          "rateLimits": [ { "interval": "MINUTE", "intervalNum": 1, "limit": 2400, "rateLimitType": "REQUEST_WEIGHT" } ],
          "serverTime": 1794735000250, "assets": [], "timezone": "UTC",
          "symbols": [
            { "symbol": "BTCUSDT", "pair": "BTCUSDT", "contractType": "PERPETUAL", "deliveryDate": 4133404800000,
              "onboardDate": 1569398400000, "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDT", "marginAsset": "USDT",
              "pricePrecision": 2, "quantityPrecision": 3, "underlyingType": "COIN",
              "filters": [
                { "filterType": "PRICE_FILTER", "maxPrice": "4529764", "minPrice": "556.80", "tickSize": "0.10" },
                { "filterType": "LOT_SIZE", "maxQty": "1000", "minQty": "0.001", "stepSize": "0.001" },
                { "filterType": "MARKET_LOT_SIZE", "maxQty": "120", "minQty": "0.001", "stepSize": "0.001" },
                { "filterType": "MAX_NUM_ORDERS", "limit": 200 },
                { "filterType": "MIN_NOTIONAL", "notional": "100" },
                { "filterType": "PERCENT_PRICE", "multiplierUp": "1.0500", "multiplierDown": "0.9500", "multiplierDecimal": "4" }
              ], "orderTypes": [ "LIMIT", "MARKET", "STOP", "STOP_MARKET", "TAKE_PROFIT", "TAKE_PROFIT_MARKET" ],
              "timeInForce": [ "GTC", "IOC" ] },
            { "symbol": "BTCUSDT_270326", "pair": "BTCUSDT", "contractType": "CURRENT_QUARTER", "status": "TRADING",
              "baseAsset": "BTC", "quoteAsset": "USDT", "marginAsset": "USDT", "filters": [] },
            { "symbol": "ETHUSDT", "pair": "ETHUSDT", "contractType": "PERPETUAL", "status": "TRADING", "baseAsset": "ETH",
              "quoteAsset": "USDT", "marginAsset": "USDT",
              "filters": [
                { "filterType": "PRICE_FILTER", "maxPrice": "306177", "minPrice": "39.86", "tickSize": "0.01" },
                { "filterType": "LOT_SIZE", "maxQty": "10000", "minQty": "0.001", "stepSize": "0.001" },
                { "filterType": "MIN_NOTIONAL", "notional": "20" }
              ] }
          ]
        }
        """;

    /// <summary>The moment that the application's clock shows, which a signed call says it was made at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 30, 0, 250, TimeSpan.Zero);

    /// <summary>The lengths of a candle that the gateway reads, each with the name that Binance gives it.</summary>
    public static readonly (CandleInterval Interval, string Name, TimeSpan Length)[] Intervals =
    [
        (CandleInterval.FifteenMinutes, "15m", TimeSpan.FromMinutes(15)),
        (CandleInterval.OneHour, "1h", TimeSpan.FromHours(1)),
        (CandleInterval.FourHours, "4h", TimeSpan.FromHours(4)),
        (CandleInterval.OneDay, "1d", TimeSpan.FromDays(1)),
        (CandleInterval.OneWeek, "1w", TimeSpan.FromDays(7)),
    ];

    /// <summary>The user whose calls a test makes, where it does not matter whose they are.</summary>
    public static readonly Guid User = new("0b4c2a51-6d3e-4f7a-9c18-2e5d7f9a1b30");

    /// <summary>Both markets, each in both environments: the four addresses of the configuration file.</summary>
    public static readonly (Market Market, TradingMode Mode)[] Pairs =
    [
        (Market.Spot, TradingMode.Live),
        (Market.Spot, TradingMode.Demo),
        (Market.Futures, TradingMode.Live),
        (Market.Futures, TradingMode.Demo),
    ];

    /// <summary><see cref="Pairs"/>, for a test that runs once for each.</summary>
    public static TheoryData<Market, TradingMode> MarketsAndEnvironments
    {
        get
        {
            var pairs = new TheoryData<Market, TradingMode>();
            foreach (var (market, mode) in Pairs)
            {
                pairs.Add(market, mode);
            }

            return pairs;
        }
    }

    /// <summary>The address that the configuration file of the tests gives a market in an environment, such as <c>https://spot-live.example</c>.</summary>
    public static string AddressOf(Market market, TradingMode mode) => $"https://{HostOf(market, mode)}";

    public static string HostOf(Market market, TradingMode mode) =>
        $"{market.ToString().ToLowerInvariant()}-{mode.ToString().ToLowerInvariant()}.example";

    /// <summary>
    /// A user's credential for an environment: each environment has one of its own. Neither key
    /// has the shape of a real one, so that nothing masks it for its shape.
    /// </summary>
    public static (string ApiKey, string SecretKey) CredentialOf(TradingMode mode) => mode switch
    {
        TradingMode.Live => ("binance-live-api-0123456789-abcdefgh-Lm3N", "binance-live-sealed-0123456789-abcd-Zz9Y"),
        TradingMode.Demo => ("binance-demo-api-0123456789-abcdefgh-Qr5T", "binance-demo-sealed-0123456789-abcd-Vw7X"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>A Binance that answers every call the gateway makes with what its documentation gives for it.</summary>
    /// <param name="with">Adds to an answer before it is sent, such as a header.</param>
    public static StandInSystem ThatAnswers(Action<HttpResponseMessage>? with = null) =>
        new(request =>
        {
            var answer = StandInSystem.Json(HttpStatusCode.OK, AnswerTo(request.RequestUri!.AbsolutePath));
            with?.Invoke(answer);

            return answer;
        });

    /// <summary>What Binance answers a call to a path with.</summary>
    public static string AnswerTo(string path) => path switch
    {
        "/api/v3/time" or "/fapi/v1/time" => Time,
        "/api/v3/account" => SpotAccount,
        "/fapi/v3/balance" => FuturesBalances,
        "/fapi/v1/accountConfig" => FuturesAccountSettings,
        "/sapi/v1/account/apiRestrictions" => Restrictions,

        // The market data. Candles are of a quarter of an hour, as Binance has them at Now.
        "/api/v3/klines" or "/fapi/v1/klines" => CandlesAt(Now, CandleInterval.FifteenMinutes),
        "/api/v3/ticker/price" => SpotPrice,
        "/fapi/v2/ticker/price" => FuturesPrice,
        "/api/v3/exchangeInfo" => SpotSymbols,
        "/fapi/v1/exchangeInfo" => FuturesSymbols,
        "/fapi/v1/premiumIndex" => MarkPrice,
        "/fapi/v1/openInterest" => OpenInterest,
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "The gateway is not known to make this call."),
    };

    /// <summary>
    /// The candles that Binance answers with at a moment of its clock: the newest, oldest first,
    /// and last the one that is in progress then. Each is a row as Binance's documentation gives
    /// it, with amounts as text.
    /// </summary>
    /// <param name="at">What Binance's clock shows.</param>
    /// <param name="interval">The length of a candle.</param>
    /// <param name="closed">How many closed candles come before the one in progress.</param>
    public static string CandlesAt(DateTimeOffset at, CandleInterval interval, int closed = 3)
    {
        var length = LengthOf(interval);
        var began = BeganAt(at, interval);
        var rows = Enumerable.Range(0, closed + 1).Select(row => RowOf(CandleOf(began - (length * (closed - row)), interval)));

        return $"[{string.Join(",", rows)}]";
    }

    /// <summary>What the gateway gives of <see cref="CandlesAt"/>: the closed candles, without the one in progress.</summary>
    public static IReadOnlyList<ExchangeCandle> ClosedAt(DateTimeOffset at, CandleInterval interval, int closed = 3)
    {
        var length = LengthOf(interval);
        var began = BeganAt(at, interval);

        return [.. Enumerable.Range(0, closed).Select(row => CandleOf(began - (length * (closed - row)), interval))];
    }

    /// <summary>
    /// When the candle that is in progress at a moment began. Binance's candles begin at the
    /// multiples of their length since midnight in UTC, and its weeks on a Monday.
    /// </summary>
    public static DateTimeOffset BeganAt(DateTimeOffset at, CandleInterval interval)
    {
        // The first day that ticks count from was a Monday, so this holds for a week too.
        var ticks = at.UtcTicks;

        return new DateTimeOffset(ticks - (ticks % LengthOf(interval).Ticks), TimeSpan.Zero);
    }

    public static TimeSpan LengthOf(CandleInterval interval) => Intervals.Single(known => known.Interval == interval).Length;

    /// <summary>The length of the candles that a call asks for.</summary>
    public static CandleInterval IntervalOf(HttpRequestMessage request)
    {
        var name = request.RequestUri!.Query.TrimStart('?').Split('&').Single(parameter => parameter.StartsWith("interval=", StringComparison.Ordinal))["interval=".Length..];

        return Intervals.Single(known => known.Name == name).Interval;
    }

    /// <summary>A candle that began at a moment, with prices and a volume that are its own.</summary>
    private static ExchangeCandle CandleOf(DateTimeOffset opened, CandleInterval interval)
    {
        var own = opened.ToUnixTimeSeconds() / 900 % 1000;
        var open = 104000.12345678m + own;

        return new ExchangeCandle(opened, opened + LengthOf(interval), open, open + 50.5m, open - 40.25m, open + 10.125m, own + 0.00000001m);
    }

    /// <summary>A candle as a row of Binance's answer. Its last millisecond is the one before the next candle begins.</summary>
    private static string RowOf(ExchangeCandle candle)
    {
        string Text(decimal amount) => $"\"{amount.ToString("0.00000000", CultureInfo.InvariantCulture)}\"";

        return $"[{candle.OpenedAt.ToUnixTimeMilliseconds()},{Text(candle.Open)},{Text(candle.High)},{Text(candle.Low)},{Text(candle.Close)},"
            + $"{Text(candle.Volume)},{candle.ClosedAt.ToUnixTimeMilliseconds() - 1},\"2434.19055334\",308,\"1756.87402397\",\"28.46694368\",\"0\"]";
    }

    /// <summary>The application with a Binance of the test's behind the client of the gateway, and a clock that stands at <see cref="Now"/>.</summary>
    /// <param name="binance">What answers in Binance's place.</param>
    /// <param name="debug">Whether the log is at the debug level, at which the bodies of calls are written too.</param>
    /// <param name="clock">The clock of the application, when it is not one that stands still.</param>
    /// <param name="share">
    /// The part of Binance's limit on requests that the service allows itself, in percent, when
    /// it is not that of the usual file, which is far more than a test uses.
    /// </param>
    /// <param name="database">The database, when it is not the shared one of the test server.</param>
    /// <param name="watchesClock">
    /// Whether the application reads Binance's clocks on its schedule. It does in a test of the
    /// watch on the clock, on a database of the test's own, where its application runs the jobs.
    /// </param>
    public static LoggedApplication Behind(
        StandInSystem binance,
        bool debug = false,
        TimeProvider? clock = null,
        int? share = null,
        TestDatabase? database = null,
        bool watchesClock = false)
    {
        var settings = LoggedApplication.Settings();
        if (debug)
        {
            settings = settings.With("Logging:MinimumLevel", "Debug");
        }

        if (share is { } percent)
        {
            settings = settings.With("Binance:LimitSharePercent", percent);
        }

        return binance.Behind(
            ExchangeGatewayModule.HttpClientName,
            settings,
            services => services.AddSingleton<TimeProvider>(clock ?? new TestClock(Now)),
            database,
            watchesClock);
    }

    /// <summary>
    /// What Binance works out to see that a call was signed with a secret key: the HMAC-SHA256 of
    /// the query as it was sent, without the signature, in hexadecimal.
    /// </summary>
    public static string SignatureOf(ReceivedCall call, string secretKey)
    {
        var query = call.Address.Query.TrimStart('?');
        var signed = query[..query.LastIndexOf("&signature=", StringComparison.Ordinal)];

        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(signed)));
    }
}
