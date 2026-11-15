using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway.Adapters.Binance;

/// <summary>
/// One call of Binance's API that the gateway makes: the market whose address serves it, its
/// path, and its query where it has one of its own. These are all of them. <see cref="BinanceCall"/>
/// takes nothing else, and nothing outside this class can make one, so what the gateway can send
/// to Binance is this list. See docs/exchange.md.
/// </summary>
/// <remarks>
/// Every one of them reads. None withdraws, deposits, or transfers funds, between the Spot and
/// the Futures wallet of a user included (FR-BIN-9): Binance keeps those calls in its wallet API,
/// under <c>sapi</c>, of which the gateway makes one call, which reads the permissions of a key.
/// </remarks>
internal sealed class BinanceEndpoint
{
    /// <summary>The clock of the Spot market.</summary>
    public static readonly BinanceEndpoint SpotTime = new(Market.Spot, "api/v3/time");

    /// <summary>The clock of the Futures market.</summary>
    public static readonly BinanceEndpoint FuturesTime = new(Market.Futures, "fapi/v1/time");

    /// <summary>
    /// What an API key may do. It is of Binance's wallet API, which the address of the Spot market
    /// serves on the real exchange. The test environment has no such call.
    /// </summary>
    public static readonly BinanceEndpoint KeyPermissions = new(Market.Spot, "sapi/v1/account/apiRestrictions");

    /// <summary>The account of the Spot market: whether it may trade, and what it holds, without the assets it holds none of.</summary>
    public static readonly BinanceEndpoint SpotAccount = new(Market.Spot, "api/v3/account", "omitZeroBalances=true");

    /// <summary>The settings of the account of the Futures market, which say whether it may trade.</summary>
    public static readonly BinanceEndpoint FuturesAccountSettings = new(Market.Futures, "fapi/v1/accountConfig");

    /// <summary>What the account of the Futures market holds.</summary>
    public static readonly BinanceEndpoint FuturesBalances = new(Market.Futures, "fapi/v3/balance");

    private BinanceEndpoint(Market market, string path, string query = "")
    {
        Market = market;
        Path = path;
        Query = query;
    }

    /// <summary>The market whose address of the configuration file serves the call.</summary>
    public Market Market { get; }

    /// <summary>The path of the call, under that address.</summary>
    public string Path { get; }

    /// <summary>
    /// The parameters that the call is always made with, as they are sent, or nothing. No call
    /// takes a parameter of its caller yet.
    /// </summary>
    public string Query { get; }
}
