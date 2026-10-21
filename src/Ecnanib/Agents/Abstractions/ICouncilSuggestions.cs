using Ecnanib.Ai.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>The provider and model with the best expectancy in the user's own history for a role (FR-EXP-08), over <see cref="Trades"/> closed trades.</summary>
public sealed record CouncilSuggestion(CouncilRole Role, AiProvider Provider, string Model, decimal Expectancy, int Trades);

/// <summary>
/// Per-role council suggestions from the user's own experience at a timeframe and market (FR-EXP-08, FR-SYNC-05).
/// A role without a suggestion falls back to the Appendix B default. Seeded by the agents module with no
/// suggestions; the experience module provides the real ones. Register an implementation before <c>AddAgents</c>
/// to replace the default.
/// </summary>
public interface ICouncilSuggestions
{
    Task<IReadOnlyList<CouncilSuggestion>> SuggestAsync(Guid userId, CandleInterval timeframe, MarketType marketType, CancellationToken cancellationToken);
}
