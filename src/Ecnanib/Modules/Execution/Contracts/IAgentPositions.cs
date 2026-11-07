using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>
/// An agent's position as Execution's records hold it, for the agent's overview (FR-AGT-11). It reads the database
/// only, never Binance, so showing it costs no request weight and takes no lock, unlike
/// <see cref="IAgentAccounts.ReadAsync"/>, which reconciles first. Reconciliation keeps the records in line with
/// Binance every <c>ReconcileIntervalSeconds</c>, and <see cref="IExecutionObserver"/> says when they change. See The
/// agent's page in docs/architecture.md.
/// </summary>
public interface IAgentPositions
{
    /// <summary>The agent's open position and pending entry, or <see cref="RecordedPosition.None"/>. Scoped to the user (NFR-SEC-04).</summary>
    Task<RecordedPosition> FindAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);
}

/// <param name="Position">
/// The parts its protections hold, as one position: their quantity, average entry, the tightest stop and the nearest
/// target. No break-even price, which only Binance has. Null when nothing is held.
/// </param>
/// <param name="OpenedAt">When its first part filled. Null without a position.</param>
/// <param name="Targets">Its distinct take-profits, nearest first. Its parts can have different ones.</param>
/// <param name="PendingEntry">An entry that hasn't filled yet, or null.</param>
/// <param name="Market">The market of the orders, from their own rows, never the agent's: null when there are none.</param>
/// <param name="Environment">Demo or Live, from the orders' own rows, which is what to label it (FR-UI-07).</param>
/// <param name="Symbol">The pair the orders are in.</param>
public sealed record RecordedPosition(
    OpenPosition? Position,
    DateTimeOffset? OpenedAt,
    IReadOnlyList<decimal> Targets,
    AgentPendingEntry? PendingEntry,
    Market? Market,
    TradingEnvironment? Environment,
    string? Symbol)
{
    /// <summary>Kept to as many places as an average entry, like Binance's own figures.</summary>
    public const int PnlPlaces = 8;

    public static RecordedPosition None { get; } = new(null, null, [], null, null, null, null);

    /// <summary>
    /// What the position would make or lose closed at <paramref name="price"/>, before fees and funding, in the quote
    /// asset: a decision cycle's sum. Use the mark price on Futures, and the bid for a long or the ask for a short on
    /// Spot, as a cycle does. Null without a position or a price.
    /// </summary>
    public decimal? UnrealizedPnl(decimal? price) =>
        Position is { } position && price is > 0m and var at
            ? Math.Round((position.Side == PositionSide.Long ? 1m : -1m) * (at - position.AverageEntry) * position.Quantity, PnlPlaces)
            : null;
}
