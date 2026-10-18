using Binance.Net;
using Binance.Net.Enums;
using Binance.Net.Objects.Options;
using CryptoExchange.Net.Objects;
using Ecnanib.Exchange.Configuration;

namespace Ecnanib.Exchange.Binance;

/// <summary>Builds Binance.Net client options from the validated exchange configuration. One place for every library default.</summary>
internal static class BinanceOptionsBuilder
{
    public static BinanceRestOptions CreateRestOptions(ExchangeOptions options, BinanceEnvironment environment, BinanceCredentials? credentials)
    {
        var rest = new BinanceRestOptions
        {
            Environment = environment,
            ApiCredentials = credentials,
            RequestTimeout = options.RequestTimeout,
            ReceiveWindow = options.ReceiveWindow,
            // EX-05: client-side gate on the published limits; wait instead of failing (429/418 Retry-After is honoured by the library).
            RateLimiterEnabled = true,
            RateLimitingBehaviour = RateLimitingBehaviour.Wait,
            // FR-LOG-05: never surface raw request/response data (it contains signed URLs).
            OutputOriginalData = false,
            // FR-EXE-07: client order ids must round-trip unchanged.
            AllowAppendingClientOrderId = false,
            AutoTimestamp = true,
        };
        // FR-EXE-04: the execution module rounds to filters explicitly; the library must not alter or reject quantities.
        rest.SpotOptions.TradeRulesBehaviour = TradeRulesBehaviour.None;
        rest.UsdFuturesOptions.TradeRulesBehaviour = TradeRulesBehaviour.None;
        return rest;
    }

    public static BinanceSocketOptions CreateSocketOptions(ExchangeOptions options, BinanceEnvironment environment, BinanceCredentials? credentials)
    {
        return new BinanceSocketOptions
        {
            Environment = environment,
            ApiCredentials = credentials,
            RequestTimeout = options.RequestTimeout,
            RateLimiterEnabled = true,
            RateLimitingBehaviour = RateLimitingBehaviour.Wait,
            OutputOriginalData = false,
            AllowAppendingClientOrderId = false,
            // FR-DATA-03: streams reconnect automatically; our own per-stream watchdog handles silence.
            ReconnectPolicy = ReconnectPolicy.ExponentialBackoff,
            ReconnectInterval = options.Streams.ReconnectInitialDelay,
            ConnectDelayAfterRateLimited = TimeSpan.FromSeconds(30),
        };
    }
}
