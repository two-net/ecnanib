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
    /// <summary>The provider that fills a role on a new agent, until its user chooses another.</summary>
    public AiProviderCode Of(Role role) => role switch
    {
        Role.MarketAnalyst => MarketAnalyst,
        Role.SentimentAnalyst => SentimentAnalyst,
        Role.Strategist => Strategist,
        Role.RiskManager => RiskManager,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The role has no default provider."),
    };

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
/// The one trading profile (FR-RSK-1): the hard bounds a user may choose a risk setting of an
/// agent within, and the default (SRS Appendix B). A three-value setting has a lowest and a
/// highest value. A two-value setting has a floor, and may be raised from it. Percentages are of
/// agent equity.
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
    Floored<decimal> MinRewardToRisk,
    Bounded<int> LeverageCap,
    Floored<decimal> LiquidationBuffer,
    Bounded<decimal> DailyLossPercent,
    Bounded<decimal> DrawdownPercent,
    Bounded<decimal> MaxEntryDistancePercent)
{
    /// <summary>
    /// The highest value that the reward-to-risk ratio and the liquidation buffer take, in the
    /// file and from a user: more than any plan reaches, and still a number a form can take.
    /// Neither has a highest value of its own (SRS Appendix B).
    /// </summary>
    public const decimal HighestFactor = 100m;

    // A liquidation price that is no further from the entry than the stop-loss is reached first.
    private const decimal NoBuffer = 1m;

    // The highest leverage Binance offers on any contract.
    private const int HighestLeverage = 125;

    internal static AggressiveSettings Read(SettingsReader file) =>
        new(
            Percent(file, "RiskPerTradePercent"),
            file.Floor("Trading:Aggressive:MinRewardToRisk", above: 0, upTo: HighestFactor),
            file.WholeBounds("Trading:Aggressive:LeverageCap", minimum: 1, maximum: HighestLeverage),
            file.Floor("Trading:Aggressive:LiquidationBuffer", above: NoBuffer, upTo: HighestFactor),
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
