using System.Globalization;
using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// One call of Binance's API that the gateway makes: the market whose address serves it, its
/// path, its query, and what it costs of which limit on requests. These are all of them.
/// <see cref="BinanceCall"/> takes nothing else, and nothing outside this class can make one, so
/// what the gateway can send to Binance is this list. See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// Every one of them reads. None withdraws, deposits, or transfers funds, between the Spot and
/// the Futures wallet of a user included (FR-BIN-9): Binance keeps those calls in its wallet API,
/// under <c>sapi</c>, of which the gateway makes one call, which reads the permissions of a key.
/// </para>
/// <para>
/// A call of the market data takes parameters of its caller: a symbol, and the length of a
/// candle. Such a call is made here as well, from the one of the list that it is, with its
/// parameters percent-encoded, which Binance requires.
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

    /// <summary>The settings of the account of the Futures market, which say whether it may trade.</summary>
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

    /// <summary>Every call, which is how the limiter knows the heaviest of each limit.</summary>
    public static readonly IReadOnlyList<BinanceEndpoint> All =
    [
        SpotTime, FuturesTime, KeyPermissions, SpotAccount, FuturesAccountSettings, FuturesBalances,
        SpotCandles, FuturesCandles, SpotPrice, FuturesPrice, SpotSymbols, FuturesSymbols, FuturesMarkPrice, FuturesOpenInterest,
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

    private BinanceEndpoint(Market market, string path, BinanceLimit limit, int weight, string query = "", bool reportsUsage = true)
    {
        Market = market;
        Path = path;
        Limit = limit;
        Weight = weight;
        Query = query;
        ReportsUsage = reportsUsage;
    }

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

        return new BinanceEndpoint(Market, Path, Limit, Weight, Query.Length == 0 ? given : $"{Query}&{given}", ReportsUsage);
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
