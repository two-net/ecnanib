using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// One call of Binance's API that the gateway makes: the market whose address serves it, its
/// path, its query where it has one of its own, and what it costs of which limit on requests.
/// These are all of them. <see cref="BinanceCall"/> takes nothing else, and nothing outside this
/// class can make one, so what the gateway can send to Binance is this list. See
/// docs/exchange.md.
/// </summary>
/// <remarks>
/// Every one of them reads. None withdraws, deposits, or transfers funds, between the Spot and
/// the Futures wallet of a user included (FR-BIN-9): Binance keeps those calls in its wallet API,
/// under <c>sapi</c>, of which the gateway makes one call, which reads the permissions of a key.
/// </remarks>
internal sealed class BinanceEndpoint
{
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

    /// <summary>Every call, which is how the limiter knows the heaviest of each limit.</summary>
    public static readonly IReadOnlyList<BinanceEndpoint> All =
        [SpotTime, FuturesTime, KeyPermissions, SpotAccount, FuturesAccountSettings, FuturesBalances];

    private BinanceEndpoint(Market market, string path, BinanceLimit limit, int weight, string query = "")
    {
        Market = market;
        Path = path;
        Limit = limit;
        Weight = weight;
        Query = query;
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
    /// The parameters that the call is always made with, as they are sent, or nothing. No call
    /// takes a parameter of its caller yet.
    /// </summary>
    public string Query { get; }
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
