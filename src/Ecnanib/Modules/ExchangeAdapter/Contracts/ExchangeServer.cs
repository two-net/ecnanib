using Ecnanib.Kernel;

namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>
/// One Binance server, with its own base URL from the configuration file (EXT-BIN-01, NFR-SEC-10).
/// Each has its own request-weight budget, clock offset and bans, because Binance counts and bans an
/// address per server. See Exchange adapter in docs/architecture.md.
/// </summary>
public enum ExchangeServer
{
    SpotLive,
    SpotDemo,
    FuturesLive,
    FuturesDemo,

    /// <summary>Public Spot Live market data only, with no keys (EXT-BIN-02).</summary>
    SpotMarketData,
}

public static class ExchangeServers
{
    public static IReadOnlyList<ExchangeServer> All { get; } = Enum.GetValues<ExchangeServer>();

    /// <summary>The server that takes signed requests for a market and environment.</summary>
    public static ExchangeServer Trading(Market market, TradingEnvironment environment) => (market, environment) switch
    {
        (Market.Spot, TradingEnvironment.Live) => ExchangeServer.SpotLive,
        (Market.Spot, TradingEnvironment.Demo) => ExchangeServer.SpotDemo,
        (Market.Futures, TradingEnvironment.Live) => ExchangeServer.FuturesLive,
        (Market.Futures, TradingEnvironment.Demo) => ExchangeServer.FuturesDemo,
        _ => throw new ArgumentOutOfRangeException(nameof(market)),
    };

    /// <summary>
    /// The server for public data. Spot Live has a market-data-only server. Demo prices differ from
    /// Live ones (EXT-BIN-08), so Demo data always comes from the Demo server.
    /// </summary>
    public static ExchangeServer Public(Market market, TradingEnvironment environment) =>
        (market, environment) is (Market.Spot, TradingEnvironment.Live)
            ? ExchangeServer.SpotMarketData
            : Trading(market, environment);

    public static Market MarketOf(ExchangeServer server) =>
        server is ExchangeServer.FuturesLive or ExchangeServer.FuturesDemo ? Market.Futures : Market.Spot;

    /// <summary>A name an operator recognises, such as "Binance Spot Live".</summary>
    public static string Describe(ExchangeServer server) => server switch
    {
        ExchangeServer.SpotLive => "Binance Spot Live",
        ExchangeServer.SpotDemo => "Binance Spot Demo",
        ExchangeServer.FuturesLive => "Binance USDⓈ-M Futures Live",
        ExchangeServer.FuturesDemo => "Binance USDⓈ-M Futures Demo",
        ExchangeServer.SpotMarketData => "Binance Spot market data",
        _ => throw new ArgumentOutOfRangeException(nameof(server)),
    };
}
