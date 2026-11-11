using Ecnanib.Shared;

namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Trading</c> section: the default team, the Aggressive profile, and the stale limits.</summary>
public sealed record TradingSettings(
    DefaultRolesSettings DefaultRoles,
    AggressiveSettings Aggressive,
    StaleSecondsSettings StaleSeconds)
{
    internal static TradingSettings Read(SettingsReader file, AiProvidersSettings aiProviders) =>
        new(
            DefaultRolesSettings.Read(file, aiProviders),
            AggressiveSettings.Read(file),
            StaleSecondsSettings.Read(file));
}

/// <summary>The provider that fills each of the four roles on a new agent (FR-TEAM-3).</summary>
public sealed record DefaultRolesSettings(
    AiProviderCode MarketAnalyst,
    AiProviderCode SentimentAnalyst,
    AiProviderCode Strategist,
    AiProviderCode RiskManager)
{
    internal static DefaultRolesSettings Read(SettingsReader file, AiProvidersSettings aiProviders)
    {
        return new DefaultRolesSettings(
            Role("MarketAnalyst"), Role("SentimentAnalyst"), Role("Strategist"), Role("RiskManager"));

        AiProviderCode Role(string role)
        {
            var key = $"Trading:DefaultRoles:{role}";
            var name = file.Text(key);
            if (name.Length == 0)
            {
                return default;
            }

            var provider = new AiProviderCode(name);
            if (aiProviders.ByCode.Count > 0 && !aiProviders.ByCode.ContainsKey(provider))
            {
                file.Problem(key, "must name a provider configured under AiProviders");
            }

            return provider;
        }
    }
}

/// <summary>
/// The one trading profile (FR-RSK-1). A three-value setting gives the hard bounds a user may
/// choose within and the default. The other limits are fixed here. Percentages are of agent equity.
/// </summary>
/// <param name="RiskPerTradePercent">The share of equity risked on one trade.</param>
/// <param name="MinRewardToRisk">The lowest reward-to-risk ratio a plan may have at entry.</param>
/// <param name="LeverageCap">The highest leverage on Futures.</param>
/// <param name="LiquidationBuffer">How many times further than the stop-loss the liquidation price must be.</param>
/// <param name="DailyLossPercent">The loss in one day that halts the agent.</param>
/// <param name="DrawdownPercent">The fall from peak equity that halts the agent.</param>
/// <param name="MaxEntryDistancePercent">How far an entry price may be from the current price.</param>
public sealed record AggressiveSettings(
    Bounded<decimal> RiskPerTradePercent,
    decimal MinRewardToRisk,
    Bounded<int> LeverageCap,
    decimal LiquidationBuffer,
    Bounded<decimal> DailyLossPercent,
    Bounded<decimal> DrawdownPercent,
    Bounded<decimal> MaxEntryDistancePercent)
{
    // The floors that SRS Appendix B fixes for the two settings that have no bounds in the file.
    private const decimal LowestRewardToRisk = 1.0m;
    private const decimal LowestLiquidationBuffer = 1.5m;

    // The highest leverage Binance offers on any contract.
    private const int HighestLeverage = 125;

    internal static AggressiveSettings Read(SettingsReader file) =>
        new(
            Percent(file, "RiskPerTradePercent"),
            file.Number("Trading:Aggressive:MinRewardToRisk", LowestRewardToRisk),
            file.WholeBounds("Trading:Aggressive:LeverageCap", minimum: 1, maximum: HighestLeverage),
            file.Number("Trading:Aggressive:LiquidationBuffer", LowestLiquidationBuffer),
            Percent(file, "DailyLossPercent"),
            Percent(file, "DrawdownPercent"),
            Percent(file, "MaxEntryDistancePercent"));

    private static Bounded<decimal> Percent(SettingsReader file, string name) =>
        file.Bounds($"Trading:Aggressive:{name}", above: 0, upTo: 100);
}

/// <summary>
/// How long after a candle closes a decision may still be acted on, per timeframe. In the file the
/// keys are the timeframe codes <c>15m</c>, <c>1H</c>, <c>4H</c>, and <c>1D</c>.
/// </summary>
public sealed record StaleSecondsSettings(int FifteenMinutes, int OneHour, int FourHours, int OneDay)
{
    internal static StaleSecondsSettings Read(SettingsReader file) =>
        new(
            file.WholeNumber("Trading:StaleSeconds:15m"),
            file.WholeNumber("Trading:StaleSeconds:1H"),
            file.WholeNumber("Trading:StaleSeconds:4H"),
            file.WholeNumber("Trading:StaleSeconds:1D"));
}
