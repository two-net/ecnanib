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
/// on the entry order's <c>stop_price</c> and the target on the decision, so the simulator rebuilds its book after a
/// restart from the database alone.
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
            placement.Side, placement.EntryType, order.Price, placement.Quantity, placement.Stop, placement.Target, placement.ExpiresAt));
        _logger.LogInformation("Paper {Type} {Side} entry placed for agent {AgentId}: {Quantity} {Symbol} (stop {Stop})", placement.EntryType, placement.Side, agent.Id, placement.Quantity, agent.Symbol, placement.Stop);
        _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, order.Id, order.Status));
        return order;
    }

    public async Task<Order> PlaceCloseAsync(Agent agent, Trade trade, Guid? decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);

        var clientOrderId = PaperClientOrderId.For(agent.Id, trade.Id, PaperClientOrderId.Close, 0);
        var now = _time.GetUtcNow();
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
            Quantity = trade.Size,
            Status = OrderStatus.New,
            ReduceOnly = true,
            IsPaper = true,
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
            return await db.Orders.AsNoTracking().SingleAsync(o => o.ClientOrderId == clientOrderId, cancellationToken).ConfigureAwait(false);
        }

        var reason = decisionId is null ? TradeExitReason.KillSwitch : TradeExitReason.Council;
        if (!_simulator.MarkClosing(agent.MarketType, agent.Symbol, trade.Id, order.Id, reason))
            _logger.LogWarning("Paper position {TradeId} is not in the simulator's book yet; its close fills after the next reload", trade.Id);
        _logger.LogInformation("Paper close placed for trade {TradeId} of agent {AgentId}", trade.Id, agent.Id);
        _events.Publish(new OrderChanged(agent.Id, agent.UserId, now, order.Id, order.Status));
        return order;
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

    public async Task<decimal> GetEquityAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var realized = await db.Trades
            .Where(t => t.AgentId == agent.Id && t.IsPaper && t.ClosedAt != null)
            .SumAsync(t => t.RealizedPnl ?? 0m, cancellationToken).ConfigureAwait(false);
        return _options.Value.StartingBalance + realized;
    }

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
        if (placement.EntryType == OrderType.Limit)
        {
            if (placement.Price is not > 0)
                throw new ArgumentException("A limit entry needs a price.", nameof(placement));
            var isLong = placement.Side == PositionSide.Long;
            if (isLong ? placement.Stop >= placement.Price : placement.Stop <= placement.Price)
                throw new ArgumentException("The stop must be on the losing side of the entry price.", nameof(placement));
            if (placement.Target is { } target && (isLong ? target <= placement.Price : target >= placement.Price))
                throw new ArgumentException("The target must be on the winning side of the entry price.", nameof(placement));
        }
    }
}
