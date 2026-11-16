namespace Ecnanib.RiskGuard;

/// <summary>
/// The limits of the Aggressive profile that the specification fixes (SRS Appendix B). They are
/// the same for every agent: the configuration file has no key for them, and no user changes
/// them (FR-RSK-2). The settings that a user adjusts are an agent's own.
/// </summary>
public static class FixedLimits
{
    /// <summary>The most that a position on Spot is worth, as a share of the agent's equity in percent (FR-RSK-5).</summary>
    public const int SpotPositionPercentOfEquity = 100;

    /// <summary>How many positions an agent has open at one time.</summary>
    public const int OpenPositionsPerAgent = 1;
}
