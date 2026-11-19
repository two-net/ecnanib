using System.Net;
using Ecnanib.Shared;

namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Binance</c> section: endpoints, symbols, and the service-wide request budget.</summary>
/// <param name="Spot">The Spot endpoints, each with the address of its account event feed where it has one.</param>
/// <param name="Futures">The USDⓈ-margined perpetual Futures endpoints, each with the address of its account event feed where it has one.</param>
/// <param name="Symbols">The symbols an agent may trade (FR-AGT-6).</param>
/// <param name="OutboundIps">The fixed addresses the service calls Binance from, shown to users for IP-restricted keys.</param>
/// <param name="LimitSharePercent">The share of Binance's request limit the service allows itself (FR-BIN-4): from 2 to 100.</param>
/// <param name="ReconcileMinutes">The time between reconciliations with the exchange.</param>
/// <param name="ClockCheckSeconds">
/// The time between two readings of Binance's clocks, which the clock of the service is held
/// against (FR-BIN-5): from half a minute to an hour.
/// </param>
/// <param name="ClockToleranceMilliseconds">
/// How far the clock of the service may be from Binance's, ahead of it or behind it, before new
/// entries pause (FR-BIN-5): from 1 to 999.
/// </param>
public sealed record BinanceSettings(
    BinanceMarketSettings Spot,
    BinanceMarketSettings Futures,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<IPAddress> OutboundIps,
    int LimitSharePercent,
    int ReconcileMinutes,
    int ClockCheckSeconds,
    int ClockToleranceMilliseconds)
{
    // The share must hold the heaviest call that the service makes, or that call would never
    // pass. That is the read of funding on the Futures market, which Binance counts as 30 of the
    // 2400 that it allows there in a minute: a hundredth of that is 24, and two hundredths are 48.
    private const int LeastLimitSharePercent = 2;

    // A reading of a clock can take 25 seconds: the longest that a call waits for its turn at
    // the limiter, and the longest that Binance may take to answer. A reading is over before
    // the next one is due.
    private const int LeastClockCheckSeconds = 30;

    // An hour. A clock that is read less often than that is not watched.
    private const int MostClockCheckSeconds = 3600;

    // Binance refuses a signed call that says it was made a second or more after Binance's own
    // time. The tolerance is less than that, so that entries pause before calls are refused.
    private const int MostClockToleranceMilliseconds = 999;

    /// <summary>The endpoint for a market in a trading mode (FR-BIN-2).</summary>
    public Uri BaseUrl(Market market, TradingMode mode)
    {
        var endpoints = market switch
        {
            Market.Spot => Spot,
            Market.Futures => Futures,
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

        return mode switch
        {
            TradingMode.Live => endpoints.Live.BaseUrl,
            TradingMode.Demo => endpoints.Demo.BaseUrl,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    /// <summary>
    /// The address of the account event feed of a market in a trading mode, or null where the
    /// file names none: the account of a user is not followed there, and is reconciled on the
    /// schedule alone (FR-BIN-7).
    /// </summary>
    public Uri? FeedUrl(Market market, TradingMode mode)
    {
        var endpoints = market switch
        {
            Market.Spot => Spot,
            Market.Futures => Futures,
            _ => throw new ArgumentOutOfRangeException(nameof(market), market, null),
        };

        return mode switch
        {
            TradingMode.Live => endpoints.Live.FeedUrl,
            TradingMode.Demo => endpoints.Demo.FeedUrl,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    internal static BinanceSettings Read(SettingsReader file)
    {
        const string symbolsKey = "Binance:Symbols";
        const string outboundIpsKey = "Binance:OutboundIps";

        var spot = BinanceMarketSettings.Read(file, "Binance:Spot");
        var futures = BinanceMarketSettings.Read(file, "Binance:Futures");

        var symbols = file.List(symbolsKey, emptyRule: "must list at least one symbol")
            .Select(key => Symbol(file, key))
            .ToList();
        file.Distinct(symbolsKey, symbols);

        var outboundIps = file.List(outboundIpsKey).Select(file.IpAddress).ToList();

        var limitSharePercent = file.WholeNumber("Binance:LimitSharePercent", minimum: LeastLimitSharePercent, maximum: 100);
        var reconcileMinutes = file.WholeNumber("Binance:ReconcileMinutes");

        var clockCheckSeconds = file.WholeNumber(
            "Binance:ClockCheckSeconds", minimum: LeastClockCheckSeconds, maximum: MostClockCheckSeconds);
        var clockToleranceMilliseconds = file.WholeNumber(
            "Binance:ClockToleranceMilliseconds", minimum: 1, maximum: MostClockToleranceMilliseconds);

        return new BinanceSettings(
            spot,
            futures,
            symbols,
            outboundIps,
            limitSharePercent,
            reconcileMinutes,
            clockCheckSeconds,
            clockToleranceMilliseconds);
    }

    private static string Symbol(SettingsReader file, string key)
    {
        var symbol = file.Text(key);
        if (symbol.Length > 0 && !symbol.All(character => character is (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
        {
            file.Problem(key, "must be a symbol in capital letters and digits, such as BTCUSDT");
            return string.Empty;
        }

        return symbol;
    }
}

/// <summary>The endpoints of one Binance market: the real exchange and its test environment.</summary>
public sealed record BinanceMarketSettings(BinanceEndpointSettings Live, BinanceEndpointSettings Demo)
{
    internal static BinanceMarketSettings Read(SettingsReader file, string key) =>
        new(
            new BinanceEndpointSettings(file.Url($"{key}:Live:BaseUrl"), file.OptionalSocketUrl($"{key}:Live:FeedUrl")),
            new BinanceEndpointSettings(file.Url($"{key}:Demo:BaseUrl"), file.OptionalSocketUrl($"{key}:Demo:FeedUrl")));
}

/// <summary>One Binance endpoint.</summary>
/// <param name="BaseUrl">The address that its calls go to.</param>
/// <param name="FeedUrl">
/// The address of its account event feed, a WebSocket that is reached over TLS, or null where
/// the file names none (FR-BIN-7). On the Spot market it is the address of Binance's WebSocket API, and on the
/// Futures market the address that its user data streams are under.
/// </param>
public sealed record BinanceEndpointSettings(Uri BaseUrl, Uri? FeedUrl = null);
