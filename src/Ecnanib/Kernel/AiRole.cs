namespace Ecnanib.Kernel;

/// <summary>
/// A role on the AI council (FR-ROLE-01). The adapters log each call with it (FR-LOG-07), and the council,
/// agents' role assignments and trading experience all key on it. Rows will store the name, so a value is
/// never renamed.
/// </summary>
public enum AiRole
{
    TechnicalAnalyst,
    SentimentAnalyst,
    Strategist,
    RiskCritic,
    Reviewer,
}
