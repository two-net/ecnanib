namespace Ecnanib.Agents.Configuration;

/// <summary>
/// The <c>RiskCaps</c> section (Appendix D "risk caps"): system-wide ceilings that no agent's own limits may exceed
/// (FR-AGT-13, FR-AGT-14, FR-EXE-04, FR-EXE-09). Agent creation and start are validated against them here; the
/// execution module enforces them again at order time.
/// </summary>
public sealed class RiskCapsOptions
{
    public const string SectionName = "RiskCaps";

    /// <summary>Appendix A: risk per trade defaults to 1% and may not exceed 2%.</summary>
    public decimal MaxRiskPerTradePercent { get; set; } = 2m;

    public int MaxLeverage { get; set; } = 20;

    public decimal MaxDailyLossPercent { get; set; } = 10m;

    public int MaxOpenPositions { get; set; } = 5;
}
