using Ecnanib.ExchangeGateway.Adapters.Binance;
using Ecnanib.Platform.BackgroundWork;
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

        // The one limiter that every one of those calls passes, for all users: Binance counts
        // its limits on requests against the address of the service (FR-BIN-4). It is one
        // object, which also says what is used.
        services.AddSingleton<BinanceLimiter>();
        services.AddSingleton<IExchangeLimits>(provider => provider.GetRequiredService<BinanceLimiter>());

        // The watch on the clock: Binance takes a signed call only when the time that it carries
        // is near Binance's own, so new entries pause while the clock of the service is not
        // (FR-BIN-5). It is one object as well, which says how the clock stands, and a job on
        // the platform's schedule reads Binance's clocks for it.
        services.AddSingleton<BinanceClock>();
        services.AddSingleton<IExchangeClock>(provider => provider.GetRequiredService<BinanceClock>());
        services.AddBackgroundJob<BinanceClockJob>();

        // The market data, which is read once and shared by all who need it, because every read
        // costs of the one budget (FR-BIN-3). It is one object, which holds what was read in
        // memory for as long as it counts, and nothing in the database (NFR-DAT-6). It reads
        // when it is asked, so it has no job.
        services.AddSingleton<IExchangeMarketData, BinanceMarketData>();

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
