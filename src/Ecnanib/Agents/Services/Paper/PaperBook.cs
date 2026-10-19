using System.Security.Cryptography;
using System.Text;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Agents.Services.Paper;

/// <summary>Deterministic client order ids for simulated orders (FR-EXE-07): the same agent, scope, purpose and sequence always give the same id.</summary>
internal static class PaperClientOrderId
{
    public const string Entry = "entry";
    public const string StopLoss = "sl";
    public const string TakeProfit = "tp";
    public const string Close = "close";

    public static string For(Guid agentId, Guid scope, string purpose, int sequence)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{agentId:N}|{scope:N}|{purpose}|{sequence}"));
        return "p" + Convert.ToHexStringLower(hash)[..31];
    }
}

/// <summary>A simulated entry waiting for its price (FR-AGT-11). The stop it carries becomes the stop-loss order at the fill (NFR-REL-03).</summary>
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
    decimal? Target,
    DateTimeOffset? ExpiresAt);

/// <summary>A simulated open position with its protective prices and the extremes seen since entry (FR-EXP-02 excursions).</summary>
internal sealed class PaperPosition
{
    public required Guid TradeId { get; init; }

    public required Guid AgentId { get; init; }

    public required Guid UserId { get; init; }

    public required PositionSide Side { get; init; }

    public required decimal EntryPrice { get; init; }

    public required decimal Size { get; init; }

    public decimal? InitialStop { get; init; }

    public decimal? Stop { get; set; }

    public Guid? StopOrderId { get; set; }

    public decimal? Target { get; set; }

    public Guid? TargetOrderId { get; set; }

    public decimal HighestPrice { get; set; }

    public decimal LowestPrice { get; set; }

    /// <summary>Set while a market close is waiting for its price.</summary>
    public Guid? CloseOrderId { get; set; }

    public TradeExitReason? CloseReason { get; set; }

    public bool IsClosing => CloseOrderId is not null;
}

internal abstract record PaperFill(DateTimeOffset At);

internal sealed record EntryFilled(PaperEntry Entry, decimal Price, bool IsMaker, DateTimeOffset At) : PaperFill(At);

internal sealed record EntryExpired(PaperEntry Entry, DateTimeOffset At) : PaperFill(At);

internal sealed record PositionClosed(PaperPosition Position, Guid? OrderId, decimal Price, bool IsMaker, TradeExitReason Reason, DateTimeOffset At) : PaperFill(At);

/// <summary>
/// The in-memory book of one symbol's simulated orders and positions, and the fill rules of FR-AGT-11 applied to each
/// price tick: market orders fill at the tick with slippage against the trader; a buy limit fills at its price once the
/// tick is at or below it (a sell limit at or above); a stop fills at the stop with slippage once the tick trades
/// through it; a target fills at its price once the tick reaches it. When one tick crosses both stop and target the
/// stop fills (the deterministic worst case). An entry filled on a tick is checked against its stop from the next
/// tick on. Pure and single-threaded: the simulator serialises calls.
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

    /// <summary>Removes the agent's pending entries and close orders and strips the protection off its positions (the orders were cancelled, FR-AGT-06).</summary>
    public int ClearAgent(Guid agentId)
    {
        var removed = _entries.RemoveAll(e => e.AgentId == agentId);
        foreach (var position in _positions.Where(p => p.AgentId == agentId))
        {
            position.Stop = null;
            position.StopOrderId = null;
            position.Target = null;
            position.TargetOrderId = null;
            position.CloseOrderId = null;
            position.CloseReason = null;
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
                fills.Add(new PositionClosed(position, position.CloseOrderId, Slipped(price, !isLong, slippageRate), IsMaker: false, position.CloseReason ?? TradeExitReason.KillSwitch, at));
                continue;
            }

            if (position.Stop is { } stop && (isLong ? price <= stop : price >= stop))
            {
                _positions.Remove(position);
                fills.Add(new PositionClosed(position, position.StopOrderId, Slipped(stop, !isLong, slippageRate), IsMaker: false, TradeExitReason.Stop, at));
                continue;
            }

            if (position.Target is { } target && (isLong ? price >= target : price <= target))
            {
                _positions.Remove(position);
                fills.Add(new PositionClosed(position, position.TargetOrderId, target, IsMaker: true, TradeExitReason.Target, at));
            }
        }

        return fills;
    }

    /// <summary>Slippage works against the trader: a buy fills above the price, a sell below.</summary>
    internal static decimal Slipped(decimal price, bool buying, decimal slippageRate) =>
        buying ? price * (1 + slippageRate) : price * (1 - slippageRate);
}
