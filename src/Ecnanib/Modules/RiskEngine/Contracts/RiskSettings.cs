namespace Ecnanib.Modules.RiskEngine.Contracts;

/// <summary>
/// An agent's risk settings (FR-RISK-02), with the SRS's defaults. Agents stores them, and the Risk Engine
/// takes them as an input, so this holds only numbers and the engine still depends on Kernel alone (rule 4).
/// <see cref="Problems"/> checks each setting's own range, and <see cref="RiskCaps"/> the global hard caps
/// (FR-RISK-11). Two settings aren't here because they can't be changed:
/// margin mode is always isolated in version 1, and a protective stop is always required.
/// </summary>
public sealed record RiskSettings
{
    /// <summary>The SRS's defaults.</summary>
    public static RiskSettings Default { get; } = new();

    /// <summary>The loss if the protective stop fills, as a percentage of allocated capital. Default 1.</summary>
    public decimal MaxRiskPerTradePercent { get; init; } = 1m;

    /// <summary>A further cap on a position's size, in the quote asset. Null means the capital allocation.</summary>
    public decimal? MaxPositionNotional { get; init; }

    /// <summary>Futures only. Default 2.</summary>
    public int MaxLeverage { get; init; } = 2;

    /// <summary>As a percentage of allocated capital. Reaching it pauses the agent until the next UTC day. Default 3.</summary>
    public decimal DailyLossLimitPercent { get; init; } = 3m;

    /// <summary>As a percentage of allocated capital. Reaching it pauses the agent until the next UTC week. Default 6.</summary>
    public decimal WeeklyLossLimitPercent { get; init; } = 6m;

    /// <summary>Losing trades in a row before the agent pauses. 0 turns it off. Default 3.</summary>
    public int ConsecutiveLossesBeforePause { get; init; } = 3;

    /// <summary>Applies when a target is set. Default 1.5.</summary>
    public decimal MinRewardToRisk { get; init; } = 1.5m;

    /// <summary>Default 4.</summary>
    public int MaxNewEntriesPerDay { get; init; } = 4;

    /// <summary>The closest a stop may be to the entry, in ATR(14) of the agent's timeframe. Default 0.5.</summary>
    public decimal MinStopDistanceAtr { get; init; } = 0.5m;

    /// <summary>The furthest a stop may be from the entry, in ATR(14) of the agent's timeframe. Default 4.</summary>
    public decimal MaxStopDistanceAtr { get; init; } = 4m;

    /// <summary>
    /// Futures only. The entry-to-liquidation distance must be at least this many times the entry-to-stop
    /// distance. Default 3.
    /// </summary>
    public decimal LiquidationBufferMultiple { get; init; } = 3m;

    /// <summary>
    /// Futures only. Entries are skipped when funding against the position is above this percentage per
    /// 8 hours. Default 0.05.
    /// </summary>
    public decimal FundingFilterPercent { get; init; } = 0.05m;

    /// <summary>Cycles to wait after a losing trade before a new entry. Default 1.</summary>
    public int CooldownCyclesAfterLoss { get; init; } = 1;

    /// <summary>
    /// The settings outside their own range, in declaration order. Empty when every setting is usable. The
    /// global caps are <see cref="RiskCaps.Problems"/>' (FR-RISK-11).
    /// </summary>
    public IReadOnlyList<RiskSetting> Problems()
    {
        var problems = new List<RiskSetting>();

        void Require(bool valid, RiskSetting setting)
        {
            if (!valid)
            {
                problems.Add(setting);
            }
        }

        Require(MaxRiskPerTradePercent is > 0m and <= RiskLimits.MaxPercent, RiskSetting.MaxRiskPerTradePercent);
        Require(MaxPositionNotional is null or > 0m, RiskSetting.MaxPositionNotional);
        Require(MaxLeverage is >= 1 and <= RiskLimits.MaxLeverage, RiskSetting.MaxLeverage);
        Require(DailyLossLimitPercent is > 0m and <= RiskLimits.MaxPercent, RiskSetting.DailyLossLimitPercent);
        Require(WeeklyLossLimitPercent is > 0m and <= RiskLimits.MaxPercent, RiskSetting.WeeklyLossLimitPercent);
        Require(ConsecutiveLossesBeforePause is >= 0 and <= RiskLimits.MaxCount, RiskSetting.ConsecutiveLossesBeforePause);
        Require(MinRewardToRisk is > 0m and <= RiskLimits.MaxMultiple, RiskSetting.MinRewardToRisk);
        Require(MaxNewEntriesPerDay is >= 1 and <= RiskLimits.MaxCount, RiskSetting.MaxNewEntriesPerDay);
        Require(MinStopDistanceAtr is > 0m and <= RiskLimits.MaxMultiple, RiskSetting.MinStopDistanceAtr);
        Require(
            MaxStopDistanceAtr > MinStopDistanceAtr && MaxStopDistanceAtr <= RiskLimits.MaxMultiple,
            RiskSetting.MaxStopDistanceAtr);
        Require(LiquidationBufferMultiple is >= 1m and <= RiskLimits.MaxMultiple, RiskSetting.LiquidationBufferMultiple);
        Require(FundingFilterPercent is > 0m and <= RiskLimits.MaxFundingPercent, RiskSetting.FundingFilterPercent);
        Require(CooldownCyclesAfterLoss is >= 0 and <= RiskLimits.MaxCount, RiskSetting.CooldownCyclesAfterLoss);

        return problems;
    }
}

/// <summary>One of <see cref="RiskSettings"/>' settings, so a form can say which one is wrong.</summary>
public enum RiskSetting
{
    MaxRiskPerTradePercent,
    MaxPositionNotional,
    MaxLeverage,
    DailyLossLimitPercent,
    WeeklyLossLimitPercent,
    ConsecutiveLossesBeforePause,
    MinRewardToRisk,
    MaxNewEntriesPerDay,
    MinStopDistanceAtr,
    MaxStopDistanceAtr,
    LiquidationBufferMultiple,
    FundingFilterPercent,
    CooldownCyclesAfterLoss,
}

/// <summary>
/// The outer bounds of each setting's own range: what makes sense at all, not what's prudent. The global
/// hard caps in configuration, <see cref="RiskCaps"/>, are far tighter (FR-RISK-11).
/// </summary>
public static class RiskLimits
{
    public const decimal MaxPercent = 100m;

    /// <summary>Binance's highest leverage on any USDⓈ-M symbol.</summary>
    public const int MaxLeverage = 125;

    public const int MaxCount = 100;

    public const decimal MaxMultiple = 100m;

    public const decimal MaxFundingPercent = 10m;
}
