namespace Ecnanib.RiskGuard;

/// <summary>
/// The limits of the Aggressive profile that the specification fixes (SRS Appendix B). They are
/// the same for every agent: the configuration file has no key for them, and no user changes
/// them (FR-RSK-2). The settings that a user adjusts are an agent's own.
/// </summary>
public static class FixedLimits
{
    /// <summary>
    /// The most that a position on Spot is worth, as a share of the agent's equity in percent
    /// (FR-RSK-5). The size of an order is held to it as it is computed: see <see cref="OrderSizing"/>.
    /// </summary>
    public const int SpotPositionPercentOfEquity = 100;

    /// <summary>
    /// How many positions an agent has open at one time. A plan that opens a position is held to
    /// it when it is checked: <see cref="GuardRule.HeldPosition"/>.
    /// </summary>
    public const int OpenPositionsPerAgent = 1;
}
