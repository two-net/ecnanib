using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
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

    /// <summary>The moment that the application's clock shows, which a signed call says it was made at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 30, 0, 250, TimeSpan.Zero);

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
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "The gateway is not known to make this call."),
    };

    /// <summary>The application with a Binance of the test's behind the client of the gateway, and a clock that stands at <see cref="Now"/>.</summary>
    /// <param name="binance">What answers in Binance's place.</param>
    /// <param name="debug">Whether the log is at the debug level, at which the bodies of calls are written too.</param>
    /// <param name="clock">The clock of the application, when it is not one that stands still.</param>
    public static LoggedApplication Behind(StandInSystem binance, bool debug = false, TimeProvider? clock = null) =>
        binance.Behind(
            ExchangeGatewayModule.HttpClientName,
            debug ? LoggedApplication.Settings().With("Logging:MinimumLevel", "Debug") : null,
            services => services.AddSingleton<TimeProvider>(clock ?? new TestClock(Now)));

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
