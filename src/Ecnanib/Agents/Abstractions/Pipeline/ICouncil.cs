using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Abstractions;

/// <summary>What every role receives (FR-AI-06), minus the experience summary that the experience issue adds.</summary>
public sealed record CouncilContext(
    Agent Agent,
    Evaluation Evaluation,
    StrategyOutput Strategy,
    MarketSnapshot Market,
    IReadOnlyList<Trade> OpenTrades,
    IReadOnlyList<Order> OpenOrders,
    bool EntriesAllowed);

/// <summary>
/// The council's answer: an unsaved <see cref="Decision"/> graph (role outputs and transcripts attached, FR-AI-14)
/// that the pipeline persists, or <see cref="Failure"/> when the Risk Manager produced none (FR-AI-12), in which
/// case nothing new is opened and protective orders stay.
/// </summary>
public sealed record CouncilResult(Decision? Decision, string? Failure)
{
    public static CouncilResult Failed(string failure) => new(null, failure);

    public static CouncilResult Decided(Decision decision) => new(decision, null);
}

/// <summary>
/// The sequential council of Appendix B (FR-AI-02). Seeded by the agents issue with a council that is not available;
/// the council issue provides the roles. Register an implementation before <c>AddAgents</c> to replace the default.
/// </summary>
public interface ICouncil
{
    Task<CouncilResult> DeliberateAsync(CouncilContext context, CancellationToken cancellationToken);
}
