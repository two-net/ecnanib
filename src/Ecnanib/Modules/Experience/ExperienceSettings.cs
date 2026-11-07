using System.Text.RegularExpressions;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Experience;

/// <summary>
/// The Experience section's settings that the metrics use. Read() is the only way in. A missing value takes its
/// default, and an invalid one stops startup with a message that names it (CFG-03). See Trading experience in
/// docs/architecture.md.
/// </summary>
internal sealed partial class ExperienceSettings
{
    public const string AnalystHorizonKey = "Experience:AnalystHorizonCandles";

    public const string UsdQuoteAssetsKey = "Experience:UsdQuoteAssets";

    public const int DefaultAnalystHorizon = 3;

    /// <summary>A day's 1D calls are scored this many days later, so more would hold their metrics back for months.</summary>
    public const int MaximumAnalystHorizon = 30;

    public static IReadOnlyList<string> DefaultUsdQuoteAssets { get; } = ["USDT", "USDC", "FDUSD"];

    public static ExperienceSettings Default { get; } = new(DefaultAnalystHorizon, DefaultUsdQuoteAssets);

    private ExperienceSettings(int analystHorizon, IEnumerable<string> usdQuoteAssets)
    {
        AnalystHorizon = analystHorizon;
        UsdQuoteAssets = usdQuoteAssets.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>How many candles after a cycle's an analyst's call is scored against (FR-EXP-02).</summary>
    public int AnalystHorizon { get; }

    /// <summary>Quote assets whose P&amp;L counts 1:1 as US dollars, so AI cost, which is in dollars, can be taken off it.</summary>
    public IReadOnlySet<string> UsdQuoteAssets { get; }

    /// <exception cref="InvalidOperationException">A setting is invalid. The message names it.</exception>
    public static ExperienceSettings Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var horizon = SettingValues.WholeNumber(configuration, AnalystHorizonKey, DefaultAnalystHorizon, 1, MaximumAnalystHorizon);
        var assets = configuration.GetSection(UsdQuoteAssetsKey).Get<string[]>();

        if (assets is null || assets.Length == 0)
        {
            return new ExperienceSettings(horizon, DefaultUsdQuoteAssets);
        }

        foreach (var asset in assets)
        {
            if (asset is null || !Asset().IsMatch(asset))
            {
                throw new InvalidOperationException($"{UsdQuoteAssetsKey} must list assets as Binance writes them, in capitals, such as USDT.");
            }
        }

        return new ExperienceSettings(horizon, assets);
    }

    [GeneratedRegex("^[A-Z0-9]{1,20}$")]
    private static partial Regex Asset();
}

/// <summary>
/// Says at startup how the metrics are counted. The host builds every hosted service before it starts any, and this
/// one takes <see cref="ExperienceSettings"/>, so an invalid setting stops startup before any migration runs (CFG-03).
/// </summary>
internal sealed class ExperienceStartup(ExperienceSettings settings, ILogger<ExperienceStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Analysts' calls are scored {AnalystHorizon} candles later, and P&L in {UsdQuoteAssets} counts as US dollars",
            settings.AnalystHorizon,
            string.Join(", ", settings.UsdQuoteAssets.Order(StringComparer.Ordinal)));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
