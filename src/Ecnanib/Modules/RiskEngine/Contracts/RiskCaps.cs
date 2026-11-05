namespace Ecnanib.Modules.RiskEngine.Contracts;

/// <summary>
/// The global hard caps in configuration (FR-RISK-11, SRS 7.3 <c>RiskCaps</c>). No agent setting may go beyond
/// them. Agents refuses settings above them on save and at activation, and the engine clamps an agent's
/// settings to them at every evaluation, in case the caps were tightened after the agent was activated.
/// The engine takes the caps from configuration, through DI, so a caller can't pass looser ones.
/// </summary>
public sealed record RiskCaps
{
    /// <summary>The values in SRS 7.3's example.</summary>
    public static RiskCaps Default { get; } = new();

    /// <summary>The highest leverage any agent may use. Default 5.</summary>
    public int MaxLeverage { get; init; } = 5;

    /// <summary>The highest risk per trade any agent may take, as a percentage of its capital. Default 2.</summary>
    public decimal MaxRiskPerTradePercent { get; init; } = 2m;

    /// <summary>The smallest liquidation buffer any agent may have. Default 2.</summary>
    public decimal MinLiquidationBufferMultiple { get; init; } = 2m;

    /// <summary>The smallest minimum reward-to-risk any agent may have. Default 1.</summary>
    public decimal MinRewardToRisk { get; init; } = 1m;

    /// <summary>The settings beyond the caps, in declaration order. Empty when every setting is within them.</summary>
    public IReadOnlyList<RiskSetting> Problems(RiskSettings settings)
    {
        var problems = new List<RiskSetting>();

        if (settings.MaxRiskPerTradePercent > MaxRiskPerTradePercent)
        {
            problems.Add(RiskSetting.MaxRiskPerTradePercent);
        }

        if (settings.MaxLeverage > MaxLeverage)
        {
            problems.Add(RiskSetting.MaxLeverage);
        }

        if (settings.MinRewardToRisk < MinRewardToRisk)
        {
            problems.Add(RiskSetting.MinRewardToRisk);
        }

        if (settings.LiquidationBufferMultiple < MinLiquidationBufferMultiple)
        {
            problems.Add(RiskSetting.LiquidationBufferMultiple);
        }

        return problems;
    }

    /// <summary>
    /// The settings with each capped one brought within its cap: the lower of the two for leverage and risk,
    /// the higher for the liquidation buffer and reward-to-risk. The others are unchanged.
    /// </summary>
    public RiskSettings Apply(RiskSettings settings) => settings with
    {
        MaxRiskPerTradePercent = Math.Min(settings.MaxRiskPerTradePercent, MaxRiskPerTradePercent),
        MaxLeverage = Math.Min(settings.MaxLeverage, MaxLeverage),
        MinRewardToRisk = Math.Max(settings.MinRewardToRisk, MinRewardToRisk),
        LiquidationBufferMultiple = Math.Max(settings.LiquidationBufferMultiple, MinLiquidationBufferMultiple),
    };

    /// <summary>The cap on a setting, or null when it has none.</summary>
    public decimal? CapOf(RiskSetting setting) => setting switch
    {
        RiskSetting.MaxRiskPerTradePercent => MaxRiskPerTradePercent,
        RiskSetting.MaxLeverage => MaxLeverage,
        RiskSetting.MinRewardToRisk => MinRewardToRisk,
        RiskSetting.LiquidationBufferMultiple => MinLiquidationBufferMultiple,
        _ => null,
    };
}
