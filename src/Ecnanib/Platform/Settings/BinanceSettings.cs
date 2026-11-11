using System.Net;
using Ecnanib.Shared;

namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Binance</c> section: endpoints, symbols, and the service-wide request budget.</summary>
/// <param name="Spot">The Spot endpoints.</param>
/// <param name="Futures">The USDⓈ-margined perpetual Futures endpoints.</param>
/// <param name="Symbols">The symbols an agent may trade (FR-AGT-6).</param>
/// <param name="OutboundIps">The fixed addresses the service calls Binance from, shown to users for IP-restricted keys.</param>
/// <param name="LimitSharePercent">The share of Binance's request limit the service allows itself (FR-BIN-4).</param>
/// <param name="ReconcileMinutes">The time between reconciliations with the exchange.</param>
public sealed record BinanceSettings(
    BinanceMarketSettings Spot,
    BinanceMarketSettings Futures,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<IPAddress> OutboundIps,
    int LimitSharePercent,
    int ReconcileMinutes)
{
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

        var limitSharePercent = file.WholeNumber("Binance:LimitSharePercent", minimum: 1, maximum: 100);
        var reconcileMinutes = file.WholeNumber("Binance:ReconcileMinutes");

        return new BinanceSettings(spot, futures, symbols, outboundIps, limitSharePercent, reconcileMinutes);
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
            new BinanceEndpointSettings(file.Url($"{key}:Live:BaseUrl")),
            new BinanceEndpointSettings(file.Url($"{key}:Demo:BaseUrl")));
}

/// <summary>One Binance endpoint.</summary>
public sealed record BinanceEndpointSettings(Uri BaseUrl);
