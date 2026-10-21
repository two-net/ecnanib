using System.Text.Json;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// Everything the execution stages need (FR-AGT-10, FR-AI-11, FR-EXE-03..06, FR-EXE-08): the agent as evaluated, the
/// evaluation row, the pinned strategy with its resolved parameters, the engine output and the market snapshot of this
/// candle, the open trades and orders as last loaded, the risk caps, the venue for the agent's mode, whether an
/// evaluation of this agent was cut short by a restart (so the exchange state must be checked before any new action,
/// NFR-REL-01), the resolved key of a live agent (null for paper), whether new entries are allowed (FR-AGT-05) and the
/// evaluation deadline (NFR-PERF-01).
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
    bool InterruptedPreviously,
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
/// the same setup again in this evaluation.
/// </summary>
public sealed record ExecutionResult(IReadOnlyList<Order> Orders, string? Note, string? Fault = null, bool EntryPlaced = false)
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
    /// The housekeeping of every evaluation with open orders or trades, run before the council (FR-EXE-06): reconcile
    /// with the exchange after a restart (FR-EXE-08), expire entries past their timeout with the optional market
    /// fallback (FR-EXE-03), and apply the strategy's breakeven and trailing rules at this candle close. Reads run on
    /// <paramref name="cancellable"/> within the service's own budget; an order already decided is placed on
    /// <paramref name="stopping"/>, because a cancelled token does not recall it.
    /// </summary>
    Task<ExecutionResult> ManageAsync(ExecutionRequest request, CancellationToken cancellable, CancellationToken stopping);

    /// <summary>Applies the council's decision (FR-AI-11, FR-EXE-04): sizes and places an entry, tightens a stop, takes a partial, closes, or does nothing. The decision is already saved.</summary>
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, Decision decision, CancellationToken cancellationToken);
}
