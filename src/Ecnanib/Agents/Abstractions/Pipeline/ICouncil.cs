using System.Text.Json;
using Ecnanib.Database.Entities;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// What every role receives (FR-AI-06): the agent as configured, the evaluation being run, the pinned strategy with
/// its resolved parameters, the engine's output, the market snapshot, the open position and orders, whether new
/// entries are allowed (Running and not Paused, FR-AGT-05) and the evaluation deadline (NFR-PERF-01) the council
/// budgets its calls within (FR-AI-13). The experience summary (FR-EXP-06) is obtained by the council itself.
/// </summary>
public sealed record CouncilContext(
    Agent Agent,
    Evaluation Evaluation,
    StrategyDescriptor Strategy,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    StrategyOutput Output,
    MarketSnapshot Market,
    IReadOnlyList<Trade> OpenTrades,
    IReadOnlyList<Order> OpenOrders,
    bool EntriesAllowed,
    DateTimeOffset DeadlineAt);

/// <summary>
/// The council's answer. <see cref="Decision"/> is the unsaved deliberation graph (role outputs and transcripts
/// attached, FR-AI-14) that the pipeline persists; it carries only the managed trade's id, never the
/// <c>Trade</c> or <c>Evaluation</c> navigation, because adding the graph would insert those rows again.
/// <see cref="Failure"/> is set when the Risk Manager produced no decision (FR-AI-12): the graph, when present, is
/// still persisted but nothing is executed, so nothing new is opened and protective orders stay.
/// <see cref="Fault"/> is set when the agent cannot be evaluated as configured, today a model identifier the
/// provider rejected (FR-SYNC-04); the pipeline moves the agent to Error with that message after recording the
/// evaluation.
/// </summary>
public sealed record CouncilResult(Decision? Decision, string? Failure, string? Fault = null)
{
    public static CouncilResult Failed(string failure, Decision? deliberation = null, string? fault = null) => new(deliberation, failure, fault);

    public static CouncilResult Decided(Decision decision) => new(decision, null);
}

/// <summary>
/// The sequential council of Appendix B (FR-AI-02). The agents module seeds a council that is not available; the
/// council module provides the roles. Register an implementation before <c>AddAgents</c> to replace the default.
/// </summary>
public interface ICouncil
{
    Task<CouncilResult> DeliberateAsync(CouncilContext context, CancellationToken cancellationToken);
}
