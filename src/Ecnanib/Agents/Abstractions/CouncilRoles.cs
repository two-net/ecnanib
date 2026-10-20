using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// The Appendix B council table (FR-AI-02, FR-AI-04): the roles in execution order, each role's default provider
/// by observed strength and its responsibility. The Risk Manager is always last and the only role that decides.
/// </summary>
public static class CouncilRoles
{
    /// <summary>Execution order (Appendix B); the Risk Manager runs last (FR-AI-02).</summary>
    public static IReadOnlyList<CouncilRole> Order { get; } =
    [
        CouncilRole.MarketAnalyst, CouncilRole.SentimentFlow, CouncilRole.CrossMarketContext, CouncilRole.RiskManager,
    ];

    public static CouncilRole Final => CouncilRole.RiskManager;

    /// <summary>1-based position in <see cref="Order"/>, stored on each role output.</summary>
    public static int Sequence(CouncilRole role) => role switch
    {
        CouncilRole.MarketAnalyst => 1,
        CouncilRole.SentimentFlow => 2,
        CouncilRole.CrossMarketContext => 3,
        CouncilRole.RiskManager => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown council role."),
    };

    /// <summary>The Appendix B default provider of a role; a starting point, not a claim of returns (A-05).</summary>
    public static AiProvider DefaultProvider(CouncilRole role) => role switch
    {
        CouncilRole.MarketAnalyst => AiProvider.Gpt,
        CouncilRole.SentimentFlow => AiProvider.Grok,
        CouncilRole.CrossMarketContext => AiProvider.Gemini,
        CouncilRole.RiskManager => AiProvider.Claude,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown council role."),
    };

    public static string Display(CouncilRole role) => role switch
    {
        CouncilRole.MarketAnalyst => "Market Analyst",
        CouncilRole.SentimentFlow => "Sentiment & Flow",
        CouncilRole.CrossMarketContext => "Cross-Market Context",
        CouncilRole.RiskManager => "Risk Manager",
        _ => role.ToString(),
    };

    /// <summary>The Appendix B responsibility, as shown to the user and given to the model.</summary>
    public static string Responsibility(CouncilRole role) => role switch
    {
        CouncilRole.MarketAnalyst => "Technical structure, setup quality, levels, invalidation.",
        CouncilRole.SentimentFlow => "Real-time sentiment, news and crowd positioning; funding and open-interest context.",
        CouncilRole.CrossMarketContext => "BTC regime, correlations, multi-timeframe alignment, calendar risk.",
        CouncilRole.RiskManager => "Validates stop, target and size, applies the limits, approves, rejects or modifies, and manages open positions.",
        _ => "",
    };

    /// <summary>The field-name form used in change history and form fields, such as <c>riskManager</c>.</summary>
    public static string CamelCase(CouncilRole role)
    {
        var text = role.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }
}
