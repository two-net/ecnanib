using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Council.Abstractions;

/// <summary>What a role's experience summary is about: the provider, model and role for the agent's timeframe and market (FR-EXP-06).</summary>
public sealed record ExperienceQuery(Guid UserId, Guid AgentId, AiProvider Provider, string Model, CouncilRole Role, CandleInterval Timeframe, MarketType MarketType);

/// <summary>
/// The compact experience summary each role prompt carries (FR-AI-06, FR-EXP-06). The council module seeds an
/// implementation that has none; the experience issue registers the real one before <c>AddCouncil</c>. Null means
/// "no experience recorded yet"; a failure is logged and treated the same, never failing the role.
/// </summary>
public interface IExperienceSummaries
{
    Task<string?> SummarizeAsync(ExperienceQuery query, CancellationToken cancellationToken);
}
