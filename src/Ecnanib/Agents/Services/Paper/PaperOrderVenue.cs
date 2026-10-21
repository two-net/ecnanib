using System.Text.Json;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Agents.Services.Paper;

/// <summary>
/// The paper venue (FR-AGT-11): orders are rows with <c>is_paper</c> and deterministic client ids (FR-EXE-07), handed
/// to the <see cref="PaperFillSimulator"/> for fills. Spot agents are long-only (FR-AGT-12). The intended stop travels
/// on the entry order's <c>stop_price</c> and the targets on the decision, so the simulator rebuilds its book after a
/// restart from the database alone. Stop and target replacements are one transaction each (the old row cancelled, the
/// new one placed), so a position is never without its protection row (NFR-REL-03).
/// </summary>
internal sealed class PaperOrderVenue : IOrderVenue
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly PaperFillSimulator _simulator;
    private readonly IAgentEvents _events;
    private readonly IOptions<PaperOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PaperOrderVenue> _logger;

    public PaperOrderVenue(
        IDbContextFactory<EcnanibDbContext> factory,
        PaperFillSimulator simulator,
        IAgentEvents events,
        IOptions<PaperOptions> options,
        TimeProvider time,
        ILogger<PaperOrderVenue> logger)
    {
        _factory = factory;
        _simulator = simulator;
        _events = events;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public AgentMode Mode => AgentMode.Paper;

    public async Task<Order> PlaceEntryAsync(Agent agent, EntryPlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(placement);
        Validate(agent, placement);

        var clientOrderId = PaperClientOrderId.For(agent.Id, placement.DecisionId, PaperClientOrderId.Entry, placement.Sequence);
        var now = _time.GetUtcNow();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            DecisionId = placement.DecisionId,
            ClientOrderId = clientOrderId,
            Purpose = OrderPurpose.Entry,
            Type = placement.EntryType,
            Side = placement.Side == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell,
            Price = placement.EntryType == OrderType.Limit ? placement.Price : null,
            StopPrice = placement.Stop,
            Quantity = placement.Quantity,
            Status = OrderStatus.New,
            IsPaper = true,
            ExpiresAt = placement.ExpiresAt,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            var existing = await db.Orders.AsNoTracking().SingleAsync(o => o.ClientOrderId == clientOrderId, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Paper entry {ClientOrderId} of agent {AgentId} was already placed; the existing order is returned", clientOrderId, agent.Id);
            return existing;
        }

        _simulator.Register(agent.MarketType, agent.Symbol, new PaperEntry(order.Id, agent.Id, agent.UserId, placement.DecisionId, agent.Timeframe,
            placement.Side, placement.EntryType, order.Price, placement.Quantity, placement.Stop, placement.Targets, placement.PartialFraction, placement.ExpiresAt));
        _logger.LogInformation("Paper {Type} {Side} entry placed for agent {AgentId}: {Quantity} {Symbol} (stop {Stop})", placement.EntryType, placement.Side, agent.Id, placement.Quantity, agent.Symbol, placement.Stop);
        _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, order.Id, order.Status));
        return order;
    }

    public async Task<Order> PlaceCloseAsync(Agent agent, Trade trade, Guid? decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);

        var now = _time.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var closes = await db.Orders.AsNoTracking().Where(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.Close).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (closes.FirstOrDefault(o => o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled) is { } pending)
        {
            _logger.LogInformation("Paper close of trade {TradeId} is already waiting for its fill; the existing order is returned", trade.Id);
            return pending;
        }

        var clientOrderId = PaperClientOrderId.For(agent.Id, trade.Id, PaperClientOrderId.Close, closes.Count);
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            DecisionId = decisionId,
            TradeId = trade.Id,
            ClientOrderId = clientOrderId,
            Purpose = OrderPurpose.Close,
            Type = OrderType.Market,
            Side = trade.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
            Quantity = OpenSize(trade),
            Status = OrderStatus.New,
            ReduceOnly = true,
            IsPaper = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            return await db.Orders.AsNoTracking().SingleAsync(o => o.ClientOrderId == clientOrderId, cancellationToken).ConfigureAwait(false);
        }

        var reason = decisionId is null ? TradeExitReason.KillSwitch : TradeExitReason.Council;
        if (!_simulator.MarkClosing(agent.MarketType, agent.Symbol, trade.Id, order.Id, reason))
            _logger.LogWarning("Paper position {TradeId} is not in the simulator's book yet; its close fills after the next reload", trade.Id);
        _logger.LogInformation("Paper close placed for trade {TradeId} of agent {AgentId}", trade.Id, agent.Id);
        _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, order.Id, order.Status));
        return order;
    }

    public async Task<Order> PlacePartialCloseAsync(Agent agent, Trade trade, decimal quantity, Guid decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);
        if (quantity <= 0 || quantity > OpenSize(trade))
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "A partial exit takes a positive quantity below the open size.");

        var now = _time.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var partials = await db.Orders.AsNoTracking().Where(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.PartialClose).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (partials.FirstOrDefault(o => o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled) is { } pending)
            return pending;

        var clientOrderId = PaperClientOrderId.For(agent.Id, trade.Id, PaperClientOrderId.PartialClose, partials.Count);
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            DecisionId = decisionId,
            TradeId = trade.Id,
            ClientOrderId = clientOrderId,
            Purpose = OrderPurpose.PartialClose,
            Type = OrderType.Market,
            Side = trade.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
            Quantity = quantity,
            Status = OrderStatus.New,
            ReduceOnly = true,
            IsPaper = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            return await db.Orders.AsNoTracking().SingleAsync(o => o.ClientOrderId == clientOrderId, cancellationToken).ConfigureAwait(false);
        }

        if (!_simulator.MarkPartial(agent.MarketType, agent.Symbol, trade.Id, order.Id, quantity))
            _logger.LogWarning("Paper position {TradeId} is not in the simulator's book yet; its partial exit fills after the next reload", trade.Id);
        _logger.LogInformation("Paper partial exit of {Quantity} placed for trade {TradeId} of agent {AgentId}", quantity, trade.Id, agent.Id);
        _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, order.Id, order.Status));
        return order;
    }

    public async Task<IReadOnlyList<Order>> ReplaceStopAsync(Agent agent, Trade trade, decimal stop, string reason, Guid? decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (stop <= 0)
            throw new ArgumentOutOfRangeException(nameof(stop), stop, "A stop must be positive.");

        var now = _time.GetUtcNow();
        Order? placed = null;
        var cancelled = new List<Guid>();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            placed = null;
            cancelled.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Trades.SingleOrDefaultAsync(t => t.Id == trade.Id && t.ClosedAt == null, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var stops = await db.Orders.Where(o => o.TradeId == row.Id && o.Purpose == OrderPurpose.StopLoss).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var old in stops.Where(o => o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            {
                old.Status = OrderStatus.Canceled;
                old.UpdatedAt = now;
                cancelled.Add(old.Id);
            }

            var order = new Order
            {
                Id = Guid.CreateVersion7(),
                AgentId = agent.Id,
                DecisionId = decisionId,
                TradeId = row.Id,
                ClientOrderId = PaperClientOrderId.For(agent.Id, row.Id, PaperClientOrderId.StopLoss, stops.Count),
                Purpose = OrderPurpose.StopLoss,
                Type = OrderType.StopMarket,
                Side = row.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                StopPrice = stop,
                Quantity = OpenSize(row),
                Status = OrderStatus.New,
                ReduceOnly = true,
                IsPaper = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Orders.Add(order);
            row.CurrentStop = stop;
            row.StopHistory = TradeJson.AppendStop(row.StopHistory, now, stop, reason);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            placed = order;
        }).ConfigureAwait(false);

        if (placed is null)
            return [];
        if (!_simulator.UpdateStop(agent.MarketType, agent.Symbol, trade.Id, stop, placed.Id))
            _logger.LogWarning("Paper position {TradeId} is not in the simulator's book yet; its new stop applies after the next reload", trade.Id);
        trade.CurrentStop = stop;
        _logger.LogInformation("Paper stop of trade {TradeId} of agent {AgentId} moved to {Stop} ({Reason})", trade.Id, agent.Id, stop, reason);
        foreach (var id in cancelled)
            _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, id, OrderStatus.Canceled));
        _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, placed.Id, OrderStatus.New));
        return [placed];
    }

    public async Task<IReadOnlyList<Order>> ReplaceTargetsAsync(Agent agent, Trade trade, IReadOnlyList<decimal> targets, Guid? decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            return [];

        var now = _time.GetUtcNow();
        var placed = new List<Order>();
        var cancelled = new List<Guid>();
        List<Order>? openTargets = null;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            placed.Clear();
            cancelled.Clear();
            openTargets = null;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Trades.SingleOrDefaultAsync(t => t.Id == trade.Id && t.ClosedAt == null, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var all = await db.Orders.Where(o => o.TradeId == row.Id && o.Purpose == OrderPurpose.TakeProfit).OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var open = all.Where(o => o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled).ToList();
            var sequence = all.Count;
            for (var i = 0; i < open.Count && i < targets.Count; i++)
            {
                var unit = open[i];
                if (unit.Price == targets[i])
                    continue;
                unit.Status = OrderStatus.Canceled;
                unit.UpdatedAt = now;
                cancelled.Add(unit.Id);
                var replacement = new Order
                {
                    Id = Guid.CreateVersion7(),
                    AgentId = agent.Id,
                    DecisionId = decisionId,
                    TradeId = row.Id,
                    ClientOrderId = PaperClientOrderId.For(agent.Id, row.Id, PaperClientOrderId.TakeProfit, sequence++),
                    Purpose = OrderPurpose.TakeProfit,
                    Type = OrderType.Limit,
                    Side = unit.Side,
                    Price = targets[i],
                    Quantity = unit.Quantity,
                    Status = OrderStatus.New,
                    ReduceOnly = true,
                    IsPaper = true,
                    CreatedAt = unit.CreatedAt,
                    UpdatedAt = now,
                };
                db.Orders.Add(replacement);
                placed.Add(replacement);
                open[i] = replacement;
            }

            row.Targets = JsonSerializer.SerializeToElement(open.Select(o => o.Price ?? 0m).ToArray());
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            openTargets = open;
        }).ConfigureAwait(false);

        if (openTargets is null || placed.Count == 0)
            return [];
        var units = PaperFillSimulator.UnitsOf(openTargets, OpenSize(trade));
        if (!_simulator.UpdateUnits(agent.MarketType, agent.Symbol, trade.Id, units))
            _logger.LogWarning("Paper position {TradeId} is not in the simulator's book yet; its new targets apply after the next reload", trade.Id);
        _logger.LogInformation("Paper targets of trade {TradeId} of agent {AgentId} replaced: {Targets}", trade.Id, agent.Id, string.Join(", ", targets));
        foreach (var id in cancelled)
            _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, id, OrderStatus.Canceled));
        foreach (var order in placed)
            _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, order.Id, OrderStatus.New));
        return placed;
    }

    public async Task<Order> ExpireEntryAsync(Agent agent, Order entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(entry);
        var now = _time.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Orders
            .Where(o => o.Id == entry.Id && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired).SetProperty(o => o.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        _simulator.RemoveEntry(agent.MarketType, agent.Symbol, entry.Id);
        var current = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == entry.Id, cancellationToken).ConfigureAwait(false);
        if (rows > 0)
        {
            _logger.LogInformation("Paper entry order {OrderId} of agent {AgentId} expired at its timeout", entry.Id, agent.Id);
            _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, entry.Id, OrderStatus.Expired));
        }

        return current;
    }

    public async Task<int> CancelOpenOrdersAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var now = _time.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = await db.Orders
            .Where(o => o.AgentId == agent.Id && o.IsPaper && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .Select(o => o.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            _simulator.ClearAgent(agent.MarketType, agent.Symbol, agent.Id);
            return 0;
        }

        var rows = await db.Orders
            .Where(o => ids.Contains(o.Id) && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Canceled).SetProperty(o => o.UpdatedAt, now), cancellationToken)
            .ConfigureAwait(false);
        _simulator.ClearAgent(agent.MarketType, agent.Symbol, agent.Id);
        _logger.LogInformation("{Orders} paper order(s) of agent {AgentId} cancelled", rows, agent.Id);
        foreach (var id in ids)
            _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, id, OrderStatus.Canceled));
        return rows;
    }

    public async Task<int> ClosePositionsAtMarketAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        List<Trade> open;
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            open = await db.Trades.AsNoTracking().Where(t => t.AgentId == agent.Id && t.IsPaper && t.ClosedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var trade in open)
            await PlaceCloseAsync(agent, trade, null, cancellationToken).ConfigureAwait(false);
        return open.Count;
    }

    /// <summary>The starting balance plus every realized paper P&amp;L of the agent, partial exits of open trades included.</summary>
    public async Task<decimal> GetEquityAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var realized = await db.Trades
            .Where(t => t.AgentId == agent.Id && t.IsPaper && t.RealizedPnl != null)
            .SumAsync(t => t.RealizedPnl ?? 0m, cancellationToken).ConfigureAwait(false);
        return _options.Value.StartingBalance + realized;
    }

    /// <summary>Equity less what the open positions and pending entries hold: the whole notional on spot, the initial margin on futures.</summary>
    public async Task<decimal> GetAvailableAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var equity = await GetEquityAsync(agent, cancellationToken).ConfigureAwait(false);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var trades = await db.Trades.AsNoTracking().Where(t => t.AgentId == agent.Id && t.IsPaper && t.ClosedAt == null)
            .Select(t => new { t.Size, t.RemainingSize, t.EntryPrice }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var entries = await db.Orders.AsNoTracking()
            .Where(o => o.AgentId == agent.Id && o.IsPaper && o.Purpose == OrderPurpose.Entry && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .Select(o => new { o.Quantity, o.Price }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var held = trades.Sum(t => (t.RemainingSize > 0 ? t.RemainingSize : t.Size) * t.EntryPrice) + entries.Sum(e => e.Quantity * (e.Price ?? 0m));
        var leverage = agent.MarketType == MarketType.UsdFutures ? Math.Max(1, agent.Leverage ?? 1) : 1;
        return equity - held / leverage;
    }

    private static decimal OpenSize(Trade trade) => trade.RemainingSize > 0 ? trade.RemainingSize : trade.Size;

    private static void Validate(Agent agent, EntryPlacement placement)
    {
        if (agent.MarketType == MarketType.Spot && placement.Side == PositionSide.Short)
            throw new ArgumentException("Spot agents are long-only (FR-AGT-12).", nameof(placement));
        if (placement.Quantity <= 0)
            throw new ArgumentException("The quantity must be positive.", nameof(placement));
        if (placement.Stop <= 0)
            throw new ArgumentException("An entry must carry a stop (NFR-REL-03).", nameof(placement));
        if (placement.EntryType is not (OrderType.Market or OrderType.Limit))
            throw new ArgumentException("Entries are market or limit orders.", nameof(placement));
        if (placement.Targets is null)
            throw new ArgumentException("The targets list is required (empty when the position trails without one).", nameof(placement));
        if (placement.EntryType == OrderType.Limit)
        {
            if (placement.Price is not > 0)
                throw new ArgumentException("A limit entry needs a price.", nameof(placement));
            var isLong = placement.Side == PositionSide.Long;
            if (isLong ? placement.Stop >= placement.Price : placement.Stop <= placement.Price)
                throw new ArgumentException("The stop must be on the losing side of the entry price.", nameof(placement));
            if (placement.Targets.Any(target => isLong ? target <= placement.Price : target >= placement.Price))
                throw new ArgumentException("Every target must be on the winning side of the entry price.", nameof(placement));
        }
    }
}
