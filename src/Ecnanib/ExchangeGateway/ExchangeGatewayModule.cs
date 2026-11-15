using Ecnanib.ExchangeGateway.Adapters.Binance;
using Ecnanib.Platform.Logging;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// Exchange Gateway: Binance market data, orders, account events, reconciliation, rate limiting
/// (SRS 3.1).
/// </summary>
public static class ExchangeGatewayModule
{
    /// <summary>
    /// The name of the client of the host's factory that every call to Binance is made with. A
    /// test puts a handler of its own behind it, in place of the network.
    /// </summary>
    public const string HttpClientName = "binance";

    public static IServiceCollection AddExchangeGateway(this IServiceCollection services)
    {
        // Binance, behind the common interface, and the one exchange that every call to it makes.
        services.AddSingleton<BinanceCall>();
        services.AddSingleton<IExchangeAdapter, BinanceAdapter>();

        // Binance reports the request weight and the order count it has counted against the
        // service in these response headers, one per interval, such as X-MBX-USED-WEIGHT-1M.
        // Its wallet API, which the check of a key calls, reports its weight under a name of its
        // own, such as X-SAPI-USED-IP-WEIGHT-1M. Every call to Binance is logged with them
        // (NFR-LOG-5).
        services.Configure<OutboundCallLogOptions>(calls =>
        {
            calls.UsageHeaderPrefixes.Add("X-MBX-USED-WEIGHT");
            calls.UsageHeaderPrefixes.Add("X-MBX-ORDER-COUNT");
            calls.UsageHeaderPrefixes.Add("X-SAPI-USED");
        });

        return services;
    }
}
