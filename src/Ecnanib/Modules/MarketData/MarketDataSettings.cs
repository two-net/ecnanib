using System.Globalization;

namespace Ecnanib.Modules.MarketData;

/// <summary>
/// Market Data's setting in the Binance section. Read() is the only way in. A missing value takes its default, and an
/// invalid one stops startup with a message that names it (CFG-03). See Live market data in docs/architecture.md.
/// </summary>
internal sealed class MarketDataSettings(TimeSpan liveMarketInterval)
{
    public const string LiveMarketIntervalKey = "Binance:LiveMarketIntervalSeconds";

    public const int DefaultLiveMarketIntervalSeconds = 10;

    /// <summary>Below this, one watched pair would cost more weight than a cycle.</summary>
    public const int MinimumLiveMarketIntervalSeconds = 5;

    public const int MaximumLiveMarketIntervalSeconds = 60;

    public static MarketDataSettings Default { get; } = new(TimeSpan.FromSeconds(DefaultLiveMarketIntervalSeconds));

    /// <summary>How often a watched pair's price and forming candle are read while a page shows them (FR-UI-05).</summary>
    public TimeSpan LiveMarketInterval { get; } = liveMarketInterval;

    /// <exception cref="InvalidOperationException">The setting is invalid. The message names it.</exception>
    public static MarketDataSettings Read(IConfiguration configuration)
    {
        var value = configuration[LiveMarketIntervalKey];

        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new InvalidOperationException($"{LiveMarketIntervalKey} must be a whole number.");
        }

        if (seconds is < MinimumLiveMarketIntervalSeconds or > MaximumLiveMarketIntervalSeconds)
        {
            throw new InvalidOperationException(
                $"{LiveMarketIntervalKey} must be between {MinimumLiveMarketIntervalSeconds} and {MaximumLiveMarketIntervalSeconds}.");
        }

        return new MarketDataSettings(TimeSpan.FromSeconds(seconds));
    }
}

/// <summary>
/// Says at startup how often watched pairs are read. The host builds every hosted service before it starts any, and
/// this one takes <see cref="MarketDataSettings"/>, so an invalid setting stops startup before any migration runs (CFG-03).
/// </summary>
internal sealed class MarketDataStartup(MarketDataSettings settings, ILogger<MarketDataStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Pairs a page shows live are read every {LiveMarketInterval}", settings.LiveMarketInterval);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
