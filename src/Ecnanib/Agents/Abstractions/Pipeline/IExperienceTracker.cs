using System.Text.Json;
using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// What the experience step sees once an evaluation has run (FR-EXP-03): the agent as evaluated, the evaluation row,
/// the strategy engine output with the resolved parameters, the market snapshot the evaluation used, the persisted
/// decision when the council ran (null when it was not needed or produced none) and whether new entries were
/// allowed (FR-AGT-05).
/// </summary>
public sealed record ExperienceContext(
    Agent Agent,
    Evaluation Evaluation,
    StrategyOutput Output,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    MarketSnapshot Market,
    Decision? Decision,
    bool EntriesAllowed);

/// <summary>
/// The "record experience" step of the pipeline, run after every evaluation whose market data loaded: shadow
/// outcomes of setups the council did not take are opened and advanced here (FR-EXP-03). Seeded by the agents module
/// with a tracker that records nothing; the experience module provides the real one. Register an implementation
/// before <c>AddAgents</c> to replace the default. A failure is logged and never changes the evaluation's outcome.
/// </summary>
public interface IExperienceTracker
{
    Task TrackAsync(ExperienceContext context, CancellationToken cancellationToken);
}
