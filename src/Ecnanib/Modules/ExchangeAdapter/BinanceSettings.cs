using Ecnanib.Modules.ExchangeAdapter.Contracts;

namespace Ecnanib.Modules.ExchangeAdapter;

/// <summary>
/// The Binance section (SRS 7.3). Read() is the only way in. The base URLs come from here and nowhere
/// else (NFR-SEC-10), so they're required, with no defaults in code. A missing or invalid value stops
/// startup with a message that names the setting (CFG-03). The section isn't bound strictly, because
/// reconciliation (#35) reads Binance:ReconcileIntervalSeconds from it.
/// </summary>
internal sealed class BinanceSettings
{
    public const string SpotLiveBaseUrlKey = "Binance:Spot:LiveBaseUrl";
    public const string SpotDemoBaseUrlKey = "Binance:Spot:DemoBaseUrl";
    public const string FuturesLiveBaseUrlKey = "Binance:Futures:LiveBaseUrl";
    public const string FuturesDemoBaseUrlKey = "Binance:Futures:DemoBaseUrl";
    public const string MarketDataBaseUrlKey = "Binance:MarketDataBaseUrl";
    public const string RecvWindowKey = "Binance:RecvWindowMs";
    public const string WeightHeadroomKey = "Binance:WeightHeadroomPercent";
    public const string ExchangeInfoRefreshKey = "Binance:ExchangeInfoRefreshHours";

    /// <summary>Binance's own default.</summary>
    private const int DefaultRecvWindowMs = 5000;

    /// <summary>
    /// Binance allows 60000, but an order whose outcome is unknown can still be accepted until its receive
    /// window has passed, and nothing may be sent again before then (FR-EXE-05). That's only remembered in
    /// memory, so the window is kept as short as the time an order is waited for.
    /// </summary>
    private const int MaximumRecvWindowMs = 10000;

    private const int DefaultWeightHeadroomPercent = 30;

    /// <summary>Above this, normal requests would have almost nothing left.</summary>
    private const int MaximumWeightHeadroomPercent = 90;

    private const int DefaultExchangeInfoRefreshHours = 24;
    private const int MaximumExchangeInfoRefreshHours = 168;

    private BinanceSettings(
        IReadOnlyDictionary<ExchangeServer, Uri> baseUrls,
        int recvWindowMs,
        int weightHeadroomPercent,
        TimeSpan exchangeInfoRefresh)
    {
        BaseUrls = baseUrls;
        RecvWindowMs = recvWindowMs;
        WeightHeadroomPercent = weightHeadroomPercent;
        ExchangeInfoRefresh = exchangeInfoRefresh;
    }

    public IReadOnlyDictionary<ExchangeServer, Uri> BaseUrls { get; }

    /// <summary>How long after its timestamp Binance still accepts a signed request (FR-EXE-10).</summary>
    public int RecvWindowMs { get; }

    /// <summary>The share of each server's weight limit kept for protective requests (EXT-BIN-04, FR-EXE-09).</summary>
    public int WeightHeadroomPercent { get; }

    public TimeSpan ExchangeInfoRefresh { get; }

    /// <exception cref="InvalidOperationException">A setting is missing or invalid. The message names it.</exception>
    public static BinanceSettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var baseUrls = new Dictionary<ExchangeServer, Uri>
        {
            [ExchangeServer.SpotLive] = ReadBaseUrl(configuration, environment, SpotLiveBaseUrlKey),
            [ExchangeServer.SpotDemo] = ReadBaseUrl(configuration, environment, SpotDemoBaseUrlKey),
            [ExchangeServer.FuturesLive] = ReadBaseUrl(configuration, environment, FuturesLiveBaseUrlKey),
            [ExchangeServer.FuturesDemo] = ReadBaseUrl(configuration, environment, FuturesDemoBaseUrlKey),
            [ExchangeServer.SpotMarketData] = ReadBaseUrl(configuration, environment, MarketDataBaseUrlKey),
        };

        var recvWindow = ReadInteger(configuration, RecvWindowKey, DefaultRecvWindowMs);
        var headroom = ReadInteger(configuration, WeightHeadroomKey, DefaultWeightHeadroomPercent);
        var refreshHours = ReadInteger(configuration, ExchangeInfoRefreshKey, DefaultExchangeInfoRefreshHours);

        Require(recvWindow is >= 1 and <= MaximumRecvWindowMs, $"{RecvWindowKey} must be between 1 and {MaximumRecvWindowMs}.");
        Require(
            headroom is >= 0 and <= MaximumWeightHeadroomPercent,
            $"{WeightHeadroomKey} must be between 0 and {MaximumWeightHeadroomPercent}.");
        Require(
            refreshHours is >= 1 and <= MaximumExchangeInfoRefreshHours,
            $"{ExchangeInfoRefreshKey} must be between 1 and {MaximumExchangeInfoRefreshHours}.");

        return new BinanceSettings(baseUrls, recvWindow, headroom, TimeSpan.FromHours(refreshHours));
    }

    /// <summary>
    /// A server's address and nothing more: an absolute URL with no path, user name, query or fragment,
    /// over https outside Development.
    /// </summary>
    private static Uri ReadBaseUrl(IConfiguration configuration, IHostEnvironment environment, string key)
    {
        var value = configuration[key]?.Trim();

        Require(!string.IsNullOrEmpty(value), $"{key} is required.");
        Require(
            Uri.TryCreate(value, UriKind.Absolute, out var url)
                && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
                && url.UserInfo.Length == 0
                && url.AbsolutePath == "/"
                && url.Query.Length == 0
                && url.Fragment.Length == 0,
            $"{key} must be a server's address only, such as https://api.binance.com.");
        Require(
            url!.Scheme == Uri.UriSchemeHttps || environment.IsDevelopment(),
            $"{key} must use https outside Development, because signed requests and keys travel to it.");

        return url;
    }

    private static int ReadInteger(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        Require(
            int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number),
            $"{key} must be a whole number.");

        return number;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
