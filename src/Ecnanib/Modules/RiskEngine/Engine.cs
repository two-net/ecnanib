using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.RiskEngine;

/// <summary>
/// The Risk Engine (SRS 3.9). It holds nothing but the caps from configuration, so it's safe to share, and each
/// evaluation is a fresh <see cref="Evaluation"/>. See Risk Engine in docs/architecture.md.
/// </summary>
internal sealed class Engine(RiskCaps caps) : IRiskEngine
{
    public RiskCaps Caps => caps;

    public RiskEvaluation Evaluate(OrderIntent intent, RiskInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(inputs);

        return new Evaluation(caps, intent, inputs).Run();
    }

    public LossLimitCheck CheckLossLimits(RiskSettings settings, decimal capitalAllocation, TradingRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(record);

        return LossLimits.Check(caps, settings, capitalAllocation, record, now);
    }
}

/// <summary>Rules that more than one evaluation shares.</summary>
internal static class Rules
{
    /// <summary>
    /// The settings are in their own range and the capital is above zero, and the caps are applied (FR-RISK-11).
    /// The trace shows each capped setting as stored, its cap, and what the rules use.
    /// </summary>
    public static RuleResult Settings(RiskCaps caps, RiskSettings settings, decimal capital)
    {
        var effective = caps.Apply(settings);
        var inputs = new Inputs()
            .Add("CapitalAllocation", capital)
            .Add("MaxRiskPerTradePercent", settings.MaxRiskPerTradePercent)
            .Add("MaxRiskPerTradePercentCap", caps.MaxRiskPerTradePercent)
            .Add("MaxRiskPerTradePercentEffective", effective.MaxRiskPerTradePercent)
            .Add("MaxLeverage", settings.MaxLeverage)
            .Add("MaxLeverageCap", caps.MaxLeverage)
            .Add("MaxLeverageEffective", effective.MaxLeverage)
            .Add("MinRewardToRisk", settings.MinRewardToRisk)
            .Add("MinRewardToRiskCap", caps.MinRewardToRisk)
            .Add("MinRewardToRiskEffective", effective.MinRewardToRisk)
            .Add("LiquidationBufferMultiple", settings.LiquidationBufferMultiple)
            .Add("LiquidationBufferMultipleCap", caps.MinLiquidationBufferMultiple)
            .Add("LiquidationBufferMultipleEffective", effective.LiquidationBufferMultiple);

        var problems = settings.Problems();

        if (problems.Count > 0)
        {
            return Trace.Fail(RiskRule.Settings, RiskReason.SettingsOutOfRange, inputs.Add("OutOfRange", string.Join(",", problems)));
        }

        return capital > 0m
            ? Trace.Pass(RiskRule.Settings, inputs)
            : Trace.Fail(RiskRule.Settings, RiskReason.NoCapital, inputs);
    }
}
