using System.ComponentModel.DataAnnotations;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Exchange.Configuration;

/// <summary>
/// The <c>exchange</c> section of the system configuration file (Appendix D): production and testnet endpoints,
/// streaming endpoints, request timeout and reconciliation interval. Contains no credentials (FR-CFG-03).
/// </summary>
public sealed class ExchangeOptions
{
    public const string SectionName = "Exchange";

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; set; } = 15;

    [Range(1000, 60000)]
    public int ReceiveWindowMs { get; set; } = 5000;

    /// <summary>Interval at which the execution module reconciles local state with the exchange (FR-EXE-08).</summary>
    [Range(5, 86400)]
    public int ReconciliationIntervalSeconds { get; set; } = 60;

    public ExchangeEndpointOptions Production { get; set; } = ExchangeEndpointOptions.ProductionDefaults();

    public ExchangeEndpointOptions Testnet { get; set; } = ExchangeEndpointOptions.TestnetDefaults();

    public ExchangeStreamOptions Streams { get; set; } = new();

    public ExchangeRetryOptions Retry { get; set; } = new();

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);

    public TimeSpan ReceiveWindow => TimeSpan.FromMilliseconds(ReceiveWindowMs);

    public TimeSpan ReconciliationInterval => TimeSpan.FromSeconds(ReconciliationIntervalSeconds);

    public ExchangeEndpointOptions EndpointsFor(ExchangeEnvironment environment) => environment switch
    {
        ExchangeEnvironment.Production => Production,
        ExchangeEnvironment.Testnet => Testnet,
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown exchange environment."),
    };
}

/// <summary>Base endpoints of one environment. Formats follow Binance.Net's presets (trailing slashes included).</summary>
public sealed class ExchangeEndpointOptions
{
    public string SpotRest { get; set; } = string.Empty;

    public string SpotStreams { get; set; } = string.Empty;

    public string SpotWsApi { get; set; } = string.Empty;

    public string FuturesRest { get; set; } = string.Empty;

    public string FuturesStreams { get; set; } = string.Empty;

    /// <summary>Optional: the futures WebSocket API host. Not available on the demo environment.</summary>
    public string? FuturesWsApi { get; set; }

    public static ExchangeEndpointOptions ProductionDefaults() => new()
    {
        SpotRest = "https://api.binance.com",
        SpotStreams = "wss://stream.binance.com:9443/",
        SpotWsApi = "wss://ws-api.binance.com:443/",
        FuturesRest = "https://fapi.binance.com",
        FuturesStreams = "wss://fstream.binance.com/",
        FuturesWsApi = "wss://ws-fapi.binance.com/",
    };

    /// <summary>Spot testnet plus the USD-M futures demo environment, per Binance's current API documentation.</summary>
    public static ExchangeEndpointOptions TestnetDefaults() => new()
    {
        SpotRest = "https://testnet.binance.vision",
        SpotStreams = "wss://stream.testnet.binance.vision",
        SpotWsApi = "wss://ws-api.testnet.binance.vision",
        FuturesRest = "https://demo-fapi.binance.com",
        FuturesStreams = "wss://demo-fstream.binance.com/",
        FuturesWsApi = null,
    };
}

public sealed class ExchangeStreamOptions
{
    /// <summary>A market stream silent for longer than this is reconnected and polling takes over meanwhile.</summary>
    public int NoDataTimeoutSeconds { get; set; } = 120;

    public int ReconnectInitialDelaySeconds { get; set; } = 2;

    public int ReconnectMaxDelaySeconds { get; set; } = 60;

    /// <summary>How long after a candle boundary the polling fallback requests the closed candle.</summary>
    public int CandleCloseGraceSeconds { get; set; } = 3;

    /// <summary>How long after a candle boundary a live stream without the closing update triggers a safety poll.</summary>
    public int LateCloseGraceSeconds { get; set; } = 10;

    public int PricePollIntervalSeconds { get; set; } = 5;

    public int MarkPricePollIntervalSeconds { get; set; } = 15;

    /// <summary>Binance mark-price stream update interval: 1000 or 3000 ms.</summary>
    public int MarkPriceUpdateIntervalMs { get; set; } = 3000;

    public int UserDataPollIntervalSeconds { get; set; } = 10;

    public TimeSpan NoDataTimeout => TimeSpan.FromSeconds(NoDataTimeoutSeconds);

    public TimeSpan ReconnectInitialDelay => TimeSpan.FromSeconds(ReconnectInitialDelaySeconds);

    public TimeSpan ReconnectMaxDelay => TimeSpan.FromSeconds(ReconnectMaxDelaySeconds);

    public TimeSpan CandleCloseGrace => TimeSpan.FromSeconds(CandleCloseGraceSeconds);

    public TimeSpan LateCloseGrace => TimeSpan.FromSeconds(LateCloseGraceSeconds);

    public TimeSpan PricePollInterval => TimeSpan.FromSeconds(PricePollIntervalSeconds);

    public TimeSpan MarkPricePollInterval => TimeSpan.FromSeconds(MarkPricePollIntervalSeconds);

    public TimeSpan UserDataPollInterval => TimeSpan.FromSeconds(UserDataPollIntervalSeconds);
}

public sealed class ExchangeRetryOptions
{
    public int MaxAttempts { get; set; } = 3;

    public int BaseDelayMs { get; set; } = 500;

    public int MaxDelayMs { get; set; } = 10000;

    /// <summary>A Retry-After longer than this is not waited for; the rate-limit exception is surfaced instead.</summary>
    public int MaxRetryAfterWaitSeconds { get; set; } = 30;

    public TimeSpan BaseDelay => TimeSpan.FromMilliseconds(BaseDelayMs);

    public TimeSpan MaxDelay => TimeSpan.FromMilliseconds(MaxDelayMs);

    public TimeSpan MaxRetryAfterWait => TimeSpan.FromSeconds(MaxRetryAfterWaitSeconds);
}
