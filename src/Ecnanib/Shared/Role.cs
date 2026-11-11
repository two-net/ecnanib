namespace Ecnanib.Shared;

/// <summary>
/// A role on the team that runs an agent (SRS 4.5). Every agent has all four (FR-TEAM-1).
/// </summary>
/// <remarks>
/// The name of a member is its code in the database and in the configuration file, so renaming
/// one changes stored data. See "Seed data" in docs/persistence.md before doing it.
/// </remarks>
public enum Role
{
    /// <summary>Reads price, volume, and indicators, and on Futures also funding and open interest.</summary>
    MarketAnalyst,

    /// <summary>Reads news and social context for the symbol and the wider market.</summary>
    SentimentAnalyst,

    /// <summary>Combines both reports, recalled lessons, and the current position into a trade plan.</summary>
    Strategist,

    /// <summary>Reviews the plan independently.</summary>
    RiskManager,
}
