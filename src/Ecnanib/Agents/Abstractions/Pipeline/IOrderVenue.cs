using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Abstractions;

/// <summary>
/// An entry with the stop it must carry (NFR-REL-03) and an optional first target. <see cref="Price"/> is the limit
/// price; null means a market entry. A paper venue keeps the stop on the entry order until the fill creates the
/// protective orders; the live venue places them after the fill is confirmed (FR-EXE-01/02).
/// </summary>
public sealed record EntryPlacement(
    Guid DecisionId,
    PositionSide Side,
    OrderType EntryType,
    decimal? Price,
    decimal Quantity,
    decimal Stop,
    decimal? Target,
    DateTimeOffset? ExpiresAt,
    int Sequence = 0);

/// <summary>
/// Where an agent's orders go: the paper venue simulates fills against live prices (FR-AGT-11), the live venue is
/// the exchange (execution issue). Lifecycle actions use it to cancel orders and close positions (FR-AGT-06); the
/// execution service uses it to place entries.
/// </summary>
public interface IOrderVenue
{
    AgentMode Mode { get; }

    /// <summary>Rejects a short entry for a spot agent (FR-AGT-12). Placing the same (decision, sequence) twice returns the first order (FR-EXE-07).</summary>
    Task<Order> PlaceEntryAsync(Agent agent, EntryPlacement placement, CancellationToken cancellationToken);

    /// <summary>A market close of an open position; <paramref name="decisionId"/> is the council decision behind it, or null for a Stop (FR-AGT-06, FR-EXE-10).</summary>
    Task<Order> PlaceCloseAsync(Agent agent, Trade trade, Guid? decisionId, CancellationToken cancellationToken);

    /// <summary>Cancels every open order of the agent; returns how many.</summary>
    Task<int> CancelOpenOrdersAsync(Agent agent, CancellationToken cancellationToken);

    /// <summary>Places a market close for every open position of the agent; returns how many.</summary>
    Task<int> ClosePositionsAtMarketAsync(Agent agent, CancellationToken cancellationToken);

    /// <summary>Account equity for sizing (FR-EXE-04): the paper starting balance plus realized paper P&amp;L, or the exchange balance.</summary>
    Task<decimal> GetEquityAsync(Agent agent, CancellationToken cancellationToken);
}

public interface IOrderVenues
{
    IOrderVenue For(AgentMode mode);
}
