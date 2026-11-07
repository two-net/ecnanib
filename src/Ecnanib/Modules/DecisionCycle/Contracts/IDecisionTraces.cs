using Ecnanib.Kernel;
using Ecnanib.Modules.AiCouncil.Contracts;

namespace Ecnanib.Modules.DecisionCycle.Contracts;

/// <summary>
/// A user's decision traces. Every call takes the user, so another user's agent behaves like one with no cycles
/// (NFR-SEC-04), and administrators never read them (FR-ADM-01). See Decision cycle in docs/architecture.md.
/// </summary>
public interface IDecisionTraces
{
    /// <summary>The agent's latest cycle, running or not, or null when it has none (FR-ROLE-08).</summary>
    Task<CycleProgress?> FindLatestAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// The agent's cycles whose candle closed in <paramref name="range"/>, oldest first, for its chart's markers
    /// (FR-UI-03): the newest <see cref="CycleRange.MaxCycles"/> at most. Only cycles in the range's environment, pair
    /// and timeframe, from each cycle's own row, so a chart never mixes Demo and Live (FR-EXP-08).
    /// </summary>
    Task<IReadOnlyList<CycleMarker>> ListCyclesAsync(Guid userId, Guid agentId, CycleRange range, CancellationToken cancellationToken);

    /// <summary>
    /// The agent's newest cycles, newest first by candle close, for its decision timeline (FR-UI-01): at most
    /// <paramref name="count"/>, and never more than <see cref="CycleRange.MaxCycles"/>. Both environments, each cycle
    /// with its own, because an agent switched to Live keeps its Demo cycles (FR-EXP-08).
    /// </summary>
    Task<IReadOnlyList<CycleSummary>> ListRecentAsync(Guid userId, Guid agentId, int count, CancellationToken cancellationToken);

    /// <summary>
    /// The whole trace of one of the agent's cycles (FR-UI-04), or null when it isn't the user's, isn't the agent's, or
    /// doesn't exist. A running cycle's has what's been saved so far.
    /// </summary>
    Task<CycleTrace?> FindAsync(Guid userId, Guid agentId, Guid cycleId, CancellationToken cancellationToken);
}

/// <summary>Which of an agent's cycles a chart shows: those of one environment, pair and timeframe, by candle close.</summary>
/// <param name="From">Cycles whose candle closed at or after this.</param>
/// <param name="To">And at or before this.</param>
public sealed record CycleRange(TradingEnvironment Environment, string Symbol, Timeframe Timeframe, DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>A chart's window at most, which the 500 candles a cycle reads would hold.</summary>
    public const int MaxCycles = 500;
}

/// <summary>One cycle, as a marker on the agent's chart.</summary>
/// <param name="CandleClose">The close of the candle that started it, which is where the marker goes.</param>
/// <param name="Outcome">How it ended, or null while it's running.</param>
/// <param name="Action">What the Strategist proposed, or null when it didn't answer, or wasn't asked.</param>
public sealed record CycleMarker(
    Guid CycleId,
    DateTimeOffset CandleClose,
    TradingEnvironment Environment,
    CycleOutcome? Outcome,
    CycleFailure? Failure,
    TradeAction? Action);
