using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// An entry with the stop it must carry (NFR-REL-03) and its targets. <see cref="Price"/> is the limit price; null
/// means a market entry. <see cref="Targets"/> are the decision's targets in order: the first is the partial target,
/// taken for <see cref="PartialFraction"/> of the size, the second (when present) the target of the remainder
/// (Appendix A). A paper venue keeps the stop on the entry order until the fill creates the protective orders; the
/// live venue places them after the fill is confirmed (FR-EXE-01/02). <see cref="Sequence"/> 1 is the market fallback
/// of an expired limit entry of the same decision (FR-EXE-03).
/// </summary>
public sealed record EntryPlacement(
    Guid DecisionId,
    PositionSide Side,
    OrderType EntryType,
    decimal? Price,
    decimal Quantity,
    decimal Stop,
    IReadOnlyList<decimal> Targets,
    decimal PartialFraction,
    DateTimeOffset? ExpiresAt,
    int Sequence = 0);

/// <summary>
/// Where an agent's orders go: the paper venue simulates fills against live prices (FR-AGT-11), the live venue is
/// the exchange (FR-EXE). Lifecycle actions use it to cancel orders and close positions (FR-AGT-06, FR-EXE-10); the
/// execution service uses it to place entries and manage positions (FR-EXE-03..06). Every placement is idempotent on
/// its deterministic client order id (FR-EXE-07): placing the same thing twice returns the first order.
/// </summary>
public interface IOrderVenue
{
    AgentMode Mode { get; }

    /// <summary>Rejects a short entry for a spot agent (FR-AGT-12). Placing the same (decision, sequence) twice returns the first order (FR-EXE-07).</summary>
    Task<Order> PlaceEntryAsync(Agent agent, EntryPlacement placement, CancellationToken cancellationToken);

    /// <summary>A market close of the whole open position; <paramref name="decisionId"/> is the council decision behind it, or null for a Stop (FR-AGT-06, FR-EXE-10). A close still open on the trade is returned instead of a second one.</summary>
    Task<Order> PlaceCloseAsync(Agent agent, Trade trade, Guid? decisionId, CancellationToken cancellationToken);

    /// <summary>A market exit of <paramref name="quantity"/> of the open position (FR-EXE-06 "partial take-profit"); the quantity is already on the lot grid and below the remaining size.</summary>
    Task<Order> PlacePartialCloseAsync(Agent agent, Trade trade, decimal quantity, Guid decisionId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the trade's stop to <paramref name="stop"/>, which only ever tightens (FR-AI-15): the replacement is in
    /// place before the old protection is removed wherever the venue allows it (NFR-REL-03). Returns the new
    /// stop-loss orders; the trade's current stop and history are updated by the venue.
    /// </summary>
    Task<IReadOnlyList<Order>> ReplaceStopAsync(Agent agent, Trade trade, decimal stop, string reason, Guid? decisionId, CancellationToken cancellationToken);

    /// <summary>Replaces the take-profit of the open units in order (the i-th open unit gets <paramref name="targets"/>[i]; a unit beyond the list keeps its target). Returns the take-profit orders placed.</summary>
    Task<IReadOnlyList<Order>> ReplaceTargetsAsync(Agent agent, Trade trade, IReadOnlyList<decimal> targets, Guid? decisionId, CancellationToken cancellationToken);

    /// <summary>Cancels an entry that reached its timeout (FR-EXE-03); returns the order as it ended, Filled when the venue found it filled meanwhile.</summary>
    Task<Order> ExpireEntryAsync(Agent agent, Order entry, CancellationToken cancellationToken);

    /// <summary>Cancels every open order of the agent; returns how many.</summary>
    Task<int> CancelOpenOrdersAsync(Agent agent, CancellationToken cancellationToken);

    /// <summary>Places a market close for every open position of the agent; returns how many.</summary>
    Task<int> ClosePositionsAtMarketAsync(Agent agent, CancellationToken cancellationToken);

    /// <summary>Account equity for sizing (FR-EXE-04): the paper starting balance plus realized paper P&amp;L, or the exchange balance.</summary>
    Task<decimal> GetEquityAsync(Agent agent, CancellationToken cancellationToken);

    /// <summary>The balance free for a new entry (FR-EXE-04): the quote asset available on spot, the available margin on futures, the paper equity less what the open positions hold.</summary>
    Task<decimal> GetAvailableAsync(Agent agent, CancellationToken cancellationToken);
}

public interface IOrderVenues
{
    IOrderVenue For(AgentMode mode);
}
