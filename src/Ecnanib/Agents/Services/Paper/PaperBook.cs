using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Services.Paper;

/// <summary>Deterministic client order ids for simulated orders (FR-EXE-07): the same agent, scope, purpose and sequence always give the same id.</summary>
internal static class PaperClientOrderId
{
    public const string Entry = ClientOrderIds.Entry;
    public const string StopLoss = ClientOrderIds.StopLoss;
    public const string TakeProfit = ClientOrderIds.TakeProfit;
    public const string Close = ClientOrderIds.Close;
    public const string PartialClose = ClientOrderIds.PartialClose;

    public static string For(Guid agentId, Guid scope, string purpose, int sequence) =>
        ClientOrderIds.For(ClientOrderIds.PaperPrefix, agentId, scope, purpose, sequence);
}

/// <summary>A simulated entry waiting for its price (FR-AGT-11). The stop it carries becomes the stop-loss order at the fill (NFR-REL-03); the targets become the units' take-profit orders.</summary>
internal sealed record PaperEntry(
    Guid OrderId,
    Guid AgentId,
    Guid UserId,
    Guid DecisionId,
    CandleInterval Timeframe,
    PositionSide Side,
    OrderType Type,
    decimal? LimitPrice,
    decimal Quantity,
    decimal Stop,
    IReadOnlyList<decimal> Targets,
    decimal PartialFraction,
    DateTimeOffset? ExpiresAt);

/// <summary>A protection unit of a simulated position: its quantity and its take-profit order, when it has one.</summary>
internal sealed class PaperUnit
{
    public required decimal Quantity { get; set; }

    public decimal? Target { get; set; }

    public Guid? TargetOrderId { get; set; }
}

/// <summary>A simulated open position with its protective prices, units and the extremes seen since entry (FR-EXP-02 excursions).</summary>
internal sealed class PaperPosition
{
    public required Guid TradeId { get; init; }

    public required Guid AgentId { get; init; }

    public required Guid UserId { get; init; }

    public required PositionSide Side { get; init; }

    public required decimal EntryPrice { get; init; }

    public required decimal Size { get; init; }

    public required decimal RemainingSize { get; set; }

    public decimal? InitialStop { get; init; }

    public decimal? Stop { get; set; }

    public Guid? StopOrderId { get; set; }

    public List<PaperUnit> Units { get; } = [];

    public decimal HighestPrice { get; set; }

    public decimal LowestPrice { get; set; }

    /// <summary>Set while a market close is waiting for its price.</summary>
    public Guid? CloseOrderId { get; set; }

    public TradeExitReason? CloseReason { get; set; }

    /// <summary>Set while a partial market exit is waiting for its price (FR-EXE-06).</summary>
    public Guid? PartialOrderId { get; set; }

    public decimal PartialQuantity { get; set; }

    public bool IsClosing => CloseOrderId is not null;
}

internal abstract record PaperFill(DateTimeOffset At);

internal sealed record EntryFilled(PaperEntry Entry, decimal Price, bool IsMaker, DateTimeOffset At) : PaperFill(At);

internal sealed record EntryExpired(PaperEntry Entry, DateTimeOffset At) : PaperFill(At);

/// <summary>A unit's target or a partial exit filled; the position stays open with <see cref="Quantity"/> less.</summary>
internal sealed record PositionReduced(PaperPosition Position, Guid? OrderId, decimal Quantity, decimal Price, bool IsMaker, TradeExitReason Reason, DateTimeOffset At) : PaperFill(At);

internal sealed record PositionClosed(PaperPosition Position, Guid? OrderId, decimal Quantity, decimal Price, bool IsMaker, TradeExitReason Reason, DateTimeOffset At) : PaperFill(At);

/// <summary>
/// The in-memory book of one symbol's simulated orders and positions, and the fill rules of FR-AGT-11 applied to each
/// price tick: market orders fill at the tick with slippage against the trader; a buy limit fills at its price once the
/// tick is at or below it (a sell limit at or above); a stop fills at the stop with slippage once the tick trades
/// through it and closes everything still open; a unit's target fills at its price once the tick reaches it and takes
/// that unit off (FR-EXE-06). When one tick crosses both stop and target the stop fills (the deterministic worst case).
/// An entry filled on a tick is checked against its stop from the next tick on. Pure and single-threaded: the
/// simulator serialises calls.
/// </summary>
internal sealed class PaperBook
{
    private readonly List<PaperEntry> _entries = [];
    private readonly List<PaperPosition> _positions = [];

    public PaperBook(MarketType market, string symbol)
    {
        Market = market;
        Symbol = symbol;
    }

    public MarketType Market { get; }

    public string Symbol { get; }

    public IReadOnlyList<PaperEntry> Entries => _entries;

    public IReadOnlyList<PaperPosition> Positions => _positions;

    public bool IsEmpty => _entries.Count == 0 && _positions.Count == 0;

    public void AddEntry(PaperEntry entry)
    {
        if (_entries.All(e => e.OrderId != entry.OrderId))
            _entries.Add(entry);
    }

    public bool RemoveEntry(Guid orderId) => _entries.RemoveAll(e => e.OrderId == orderId) > 0;

    public void AddPosition(PaperPosition position)
    {
        if (_positions.All(p => p.TradeId != position.TradeId))
            _positions.Add(position);
    }

    public PaperPosition? Position(Guid tradeId) => _positions.FirstOrDefault(p => p.TradeId == tradeId);

    public bool RemovePosition(Guid tradeId) => _positions.RemoveAll(p => p.TradeId == tradeId) > 0;

    /// <summary>Strips the stop and the targets off the agent's positions and leaves their pending exits armed: the kill switch cancels the protection once the close is placed (FR-AGT-06, NFR-REL-03).</summary>
    public void StripProtection(Guid agentId)
    {
        foreach (var position in _positions.Where(p => p.AgentId == agentId))
        {
            position.Stop = null;
            position.StopOrderId = null;
            foreach (var unit in position.Units)
            {
                unit.Target = null;
                unit.TargetOrderId = null;
            }
        }
    }

    /// <summary>Removes the agent's pending entries and exits and strips the protection off its positions (the orders were cancelled, FR-AGT-06).</summary>
    public int ClearAgent(Guid agentId)
    {
        var removed = _entries.RemoveAll(e => e.AgentId == agentId);
        foreach (var position in _positions.Where(p => p.AgentId == agentId))
        {
            position.Stop = null;
            position.StopOrderId = null;
            foreach (var unit in position.Units)
            {
                unit.Target = null;
                unit.TargetOrderId = null;
            }

            position.CloseOrderId = null;
            position.CloseReason = null;
            position.PartialOrderId = null;
            position.PartialQuantity = 0m;
        }

        return removed;
    }

    public IReadOnlyList<PaperFill> OnTick(decimal price, DateTimeOffset at, decimal slippageRate)
    {
        if (price <= 0)
            return [];

        var fills = new List<PaperFill>();
        var positions = _positions.ToArray();

        foreach (var entry in _entries.ToArray())
        {
            if (entry.ExpiresAt is { } expiresAt && expiresAt <= at)
            {
                _entries.Remove(entry);
                fills.Add(new EntryExpired(entry, at));
                continue;
            }

            var buying = entry.Side == PositionSide.Long;
            if (entry.Type == OrderType.Market)
            {
                _entries.Remove(entry);
                fills.Add(new EntryFilled(entry, Slipped(price, buying, slippageRate), IsMaker: false, at));
            }
            else if (entry.LimitPrice is { } limit && (buying ? price <= limit : price >= limit))
            {
                _entries.Remove(entry);
                fills.Add(new EntryFilled(entry, limit, IsMaker: true, at));
            }
        }

        foreach (var position in positions)
        {
            position.HighestPrice = Math.Max(position.HighestPrice, price);
            position.LowestPrice = Math.Min(position.LowestPrice, price);
            var isLong = position.Side == PositionSide.Long;

            if (position.CloseOrderId is not null)
            {
                _positions.Remove(position);
                var quantity = position.RemainingSize;
                position.RemainingSize = 0m;
                fills.Add(new PositionClosed(position, position.CloseOrderId, quantity, Slipped(price, !isLong, slippageRate), IsMaker: false, position.CloseReason ?? TradeExitReason.KillSwitch, at));
                continue;
            }

            if (position.Stop is { } stop && (isLong ? price <= stop : price >= stop))
            {
                _positions.Remove(position);
                var quantity = position.RemainingSize;
                position.RemainingSize = 0m;
                fills.Add(new PositionClosed(position, position.StopOrderId, quantity, Slipped(stop, !isLong, slippageRate), IsMaker: false, TradeExitReason.Stop, at));
                continue;
            }

            if (position.PartialOrderId is { } partialOrder)
            {
                var quantity = Math.Min(position.PartialQuantity, position.RemainingSize);
                position.PartialOrderId = null;
                position.PartialQuantity = 0m;
                Reduce(position, quantity);
                var exit = Slipped(price, !isLong, slippageRate);
                if (position.RemainingSize <= 0)
                {
                    _positions.Remove(position);
                    fills.Add(new PositionClosed(position, partialOrder, quantity, exit, IsMaker: false, TradeExitReason.Council, at));
                }
                else
                {
                    fills.Add(new PositionReduced(position, partialOrder, quantity, exit, IsMaker: false, TradeExitReason.Council, at));
                }

                continue;
            }

            foreach (var unit in position.Units.ToArray())
            {
                if (unit.Target is not { } target || !(isLong ? price >= target : price <= target))
                    continue;
                var quantity = Math.Min(unit.Quantity, position.RemainingSize);
                position.Units.Remove(unit);
                position.RemainingSize = Normalize(position.RemainingSize - quantity);
                if (position.RemainingSize <= 0)
                {
                    _positions.Remove(position);
                    fills.Add(new PositionClosed(position, unit.TargetOrderId, quantity, target, IsMaker: true, TradeExitReason.Target, at));
                    break;
                }

                fills.Add(new PositionReduced(position, unit.TargetOrderId, quantity, target, IsMaker: true, TradeExitReason.Target, at));
            }
        }

        return fills;
    }

    /// <summary>Takes a partial exit off the units from the last one (the trailing remainder) backwards.</summary>
    private static void Reduce(PaperPosition position, decimal quantity)
    {
        position.RemainingSize = Normalize(position.RemainingSize - quantity);
        var left = quantity;
        for (var i = position.Units.Count - 1; i >= 0 && left > 0; i--)
        {
            var unit = position.Units[i];
            var taken = Math.Min(unit.Quantity, left);
            unit.Quantity = Normalize(unit.Quantity - taken);
            left = Normalize(left - taken);
            if (unit.Quantity <= 0)
                position.Units.RemoveAt(i);
        }
    }

    /// <summary>Slippage works against the trader: a buy fills above the price, a sell below.</summary>
    internal static decimal Slipped(decimal price, bool buying, decimal slippageRate) =>
        buying ? price * (1 + slippageRate) : price * (1 - slippageRate);

    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;
}
