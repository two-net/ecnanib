using Ecnanib.Platform.Logging;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// Exchange Gateway: Binance market data, orders, account events, reconciliation, rate limiting
/// (SRS 3.1).
/// </summary>
public static class ExchangeGatewayModule
{
    public static IServiceCollection AddExchangeGateway(this IServiceCollection services)
    {
        // Binance reports the request weight and the order count it has counted against the
        // service in these response headers, one per interval, such as X-MBX-USED-WEIGHT-1M.
        // Every call to it is logged with them (NFR-LOG-5).
        services.Configure<OutboundCallLogOptions>(calls =>
        {
            calls.UsageHeaderPrefixes.Add("X-MBX-USED-WEIGHT");
            calls.UsageHeaderPrefixes.Add("X-MBX-ORDER-COUNT");
        });

        return services;
    }
}
