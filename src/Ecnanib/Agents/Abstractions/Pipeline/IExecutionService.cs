using System.Text.Json;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// Everything the execution stages need (FR-AGT-10, FR-AI-11, FR-EXE-03..06, FR-EXE-08): the agent as evaluated, the
/// evaluation row, the pinned strategy with its resolved parameters, the engine output and the market snapshot of this
/// candle, the open trades and orders as last loaded, the risk caps, the venue for the agent's mode, the resolved key
/// of a live agent (null for paper), whether new entries are allowed (FR-AGT-05; also false once the management step
/// blocked them, NFR-REL-03) and the evaluation deadline (NFR-PERF-01). A live agent's exchange state is verified by
/// the execution module itself before its first action in the process (NFR-REL-01), so nothing here says whether a
/// restart cut an evaluation short.
/// </summary>
public sealed record ExecutionRequest(
    Agent Agent,
    Evaluation Evaluation,
    StrategyDescriptor Strategy,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    StrategyOutput Output,
    MarketSnapshot Market,
    IReadOnlyList<Trade> OpenTrades,
    IReadOnlyList<Order> OpenOrders,
    RiskCapsOptions Caps,
    IOrderVenue Venue,
    ExchangeKeyHandle? ExchangeKey,
    bool EntriesAllowed,
    DateTimeOffset DeadlineAt)
{
    /// <summary>The close of the evaluated candle: the instant every time rule of this evaluation is judged at.</summary>
    public DateTimeOffset CandleClose => Evaluation.CandleOpenTime + Agent.Timeframe.ToTimeSpan();
}

/// <summary>
/// <see cref="Orders"/> are the orders placed; <see cref="Note"/> is shown as the evaluation's outcome detail.
/// <see cref="Fault"/> names a cause the agent cannot trade with (hedge mode on the account, FR-EXE-11): the pipeline
/// moves the agent to Error with it once the evaluation is recorded. <see cref="EntryPlaced"/> is true when the
/// management step placed an entry itself (the market fallback of FR-EXE-03), so the council is not asked to propose
/// the same setup again in this evaluation. <see cref="EntriesBlockedReason"/> says why no new entry may open this
/// candle (the exchange state could not be verified after a restart, a management step failed or ran out of its
/// budget: NFR-REL-01, NFR-REL-03); null when entries are allowed.
/// </summary>
public sealed record ExecutionResult(IReadOnlyList<Order> Orders, string? Note, string? Fault = null, bool EntryPlaced = false, string? EntriesBlockedReason = null)
{
    public static ExecutionResult Nothing(string? note = null) => new([], note);
}

/// <summary>
/// Sizing, limit enforcement, order placement and position management (FR-EXE). Seeded by the agents issue with a
/// service that executes nothing and says so; the execution module registers the real one before <c>AddAgents</c>.
/// </summary>
public interface IExecutionService
{
    /// <summary>
    /// The housekeeping of every evaluation with open orders or trades, run before the council (FR-EXE-06): verify a
    /// live agent's exchange state once per process before anything else (NFR-REL-01, FR-EXE-08), expire entries past
    /// their timeout with the optional market fallback (FR-EXE-03), and apply the strategy's breakeven and trailing
    /// rules at this candle close. A failed verification skips everything and blocks new entries; a failed step blocks
    /// new entries too (NFR-REL-03). Reads run on <paramref name="cancellable"/> within the service's own budget; an
    /// order already decided is placed on <paramref name="stopping"/>, because a cancelled token does not recall it.
    /// </summary>
    Task<ExecutionResult> ManageAsync(ExecutionRequest request, CancellationToken cancellable, CancellationToken stopping);

    /// <summary>
    /// Applies the council's decision (FR-AI-11, FR-EXE-04): sizes and places an entry, tightens a stop, takes a
    /// partial, closes, or does nothing. The decision is already saved. Every read before the first placement (the
    /// filters, the daily loss, equity and balance) runs on <paramref name="cancellable"/>, the evaluation deadline
    /// included (NFR-PERF-01), with one last check right before the first order; from that order on every placement
    /// and the writes recording it run on <paramref name="stopping"/>, because a cancelled token does not recall an
    /// order already sent, and a decision's several placements (stop, targets, partial) are finished once started so
    /// protection is never left half done.
    /// </summary>
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, Decision decision, CancellationToken cancellable, CancellationToken stopping);
}
