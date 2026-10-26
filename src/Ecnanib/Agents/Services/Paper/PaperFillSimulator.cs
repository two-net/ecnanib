using System.Text.Json;
using System.Threading.Channels;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Agents.Configuration;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Agents.Services.Paper;

/// <summary>
/// Simulates fills against live prices (FR-AGT-11): one <see cref="PaperBook"/> per symbol with open paper orders or
/// positions, fed by the production last-price stream of that symbol (paper agents need no key, FR-KEY-09), in any
/// agent state, so a Stop's close-at-market still fills. An order is armed when it is registered and fills on the next
/// tick; tick timestamps are local clock values and are never compared with order times. Fees and slippage come from
/// the <c>Paper</c> section. Every fill is persisted in its own transaction (decision, then trade, then orders); a
/// fill that cannot be persisted reloads the books from the database, which is the source of truth. A position is
/// protected per unit (FR-EXE-06): a unit's target takes that unit off, the stop closes whatever is left; every exit
/// fill is checked against the daily loss limit (FR-EXE-09).
/// </summary>
internal sealed class PaperFillSimulator : BackgroundService
{
    internal static readonly TimeSpan StartupRetry = TimeSpan.FromSeconds(30);

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly IMarketStreams _streams;
    private readonly IAgentEvents _events;
    private readonly IStrategyCatalog _catalog;
    private readonly IServiceProvider _services;
    private readonly IOptions<PaperOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PaperFillSimulator> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<(MarketType Market, string Symbol), BookState> _books = new();
    private readonly Channel<PriceArrival> _ticks = Channel.CreateUnbounded<PriceArrival>(new UnboundedChannelOptions { SingleReader = true });
    private IDailyLossLimit? _dailyLoss;

    public PaperFillSimulator(
        IDbContextFactory<EcnanibDbContext> factory,
        IMarketStreams streams,
        IAgentEvents events,
        IStrategyCatalog catalog,
        IServiceProvider services,
        IOptions<PaperOptions> options,
        TimeProvider time,
        ILogger<PaperFillSimulator> logger)
    {
        _factory = factory;
        _streams = streams;
        _events = events;
        _catalog = catalog;
        _services = services;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public int BookCount
    {
        get
        {
            lock (_lock)
                return _books.Count;
        }
    }

    public IReadOnlyList<(MarketType Market, string Symbol)> Symbols
    {
        get
        {
            lock (_lock)
                return _books.Keys.ToList();
        }
    }

    // --- Registration by the venue ----------------------------------------------------------------------------------

    public void Register(MarketType market, string symbol, PaperEntry entry)
    {
        lock (_lock)
            BookFor(market, symbol).Book.AddEntry(entry);
    }

    /// <summary>Drops a pending entry the venue expired or cancelled; false when it was not in the book.</summary>
    public bool RemoveEntry(MarketType market, string symbol, Guid orderId)
    {
        lock (_lock)
        {
            if (!_books.TryGetValue((market, symbol.ToUpperInvariant()), out var state))
                return false;
            var removed = state.Book.RemoveEntry(orderId);
            if (state.Book.IsEmpty)
                RemoveBook((market, symbol.ToUpperInvariant()));
            return removed;
        }
    }

    /// <summary>False when the position is not in the book (not loaded yet); the close then fills after the next reload.</summary>
    public bool MarkClosing(MarketType market, string symbol, Guid tradeId, Guid closeOrderId, TradeExitReason reason)
    {
        lock (_lock)
        {
            if (PositionIn(market, symbol, tradeId) is not { } position)
                return false;
            position.CloseOrderId = closeOrderId;
            position.CloseReason = reason;
            return true;
        }
    }

    /// <summary>Arms a partial market exit (FR-EXE-06); false when the position is not in the book.</summary>
    public bool MarkPartial(MarketType market, string symbol, Guid tradeId, Guid orderId, decimal quantity)
    {
        lock (_lock)
        {
            if (PositionIn(market, symbol, tradeId) is not { } position)
                return false;
            position.PartialOrderId = orderId;
            position.PartialQuantity = quantity;
            return true;
        }
    }

    /// <summary>Moves the position's stop after the venue replaced the stop-loss row; false when the position is not in the book.</summary>
    public bool UpdateStop(MarketType market, string symbol, Guid tradeId, decimal stop, Guid stopOrderId)
    {
        lock (_lock)
        {
            if (PositionIn(market, symbol, tradeId) is not { } position)
                return false;
            position.Stop = stop;
            position.StopOrderId = stopOrderId;
            return true;
        }
    }

    /// <summary>Replaces the position's units after the venue replaced the take-profit rows; false when the position is not in the book.</summary>
    public bool UpdateUnits(MarketType market, string symbol, Guid tradeId, IReadOnlyList<PaperUnit> units)
    {
        lock (_lock)
        {
            if (PositionIn(market, symbol, tradeId) is not { } position)
                return false;
            position.Units.Clear();
            position.Units.AddRange(units);
            return true;
        }
    }

    /// <summary>The positions keep their pending close or partial; only the stop and the targets go (the kill switch's last step).</summary>
    public void StripProtection(MarketType market, string symbol, Guid agentId)
    {
        lock (_lock)
        {
            if (_books.TryGetValue((market, symbol.ToUpperInvariant()), out var state))
                state.Book.StripProtection(agentId);
        }
    }

    public void ClearAgent(MarketType market, string symbol, Guid agentId)
    {
        lock (_lock)
        {
            var key = (market, symbol.ToUpperInvariant());
            if (!_books.TryGetValue(key, out var state))
                return;
            state.Book.ClearAgent(agentId);
            if (state.Book.IsEmpty)
                RemoveBook(key);
        }
    }

    private PaperPosition? PositionIn(MarketType market, string symbol, Guid tradeId) =>
        _books.TryGetValue((market, symbol.ToUpperInvariant()), out var state) ? state.Book.Position(tradeId) : null;

    // --- Hosted service -------------------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReloadAsync(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The paper fill simulator could not load its books; next attempt in {Delay}", StartupRetry);
                try
                {
                    await Task.Delay(StartupRetry, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        try
        {
            await foreach (var arrival in _ticks.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessTickAsync(arrival, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A paper price tick for {Symbol} could not be processed", arrival.Symbol);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _ticks.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var state in _books.Values)
                state.Subscription?.Dispose();
            _books.Clear();
        }
    }

    /// <summary>Rebuilds every book from the open paper orders and trades; returns the number of symbols followed.</summary>
    internal async Task<int> ReloadAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.IsPaper && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var trades = await db.Trades.AsNoTracking().Where(t => t.IsPaper && t.ClosedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        var agentIds = orders.Select(o => o.AgentId).Concat(trades.Select(t => t.AgentId)).Distinct().ToList();
        var agents = await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.UserId, a.MarketType, a.Symbol, a.Timeframe, a.StrategyId, a.StrategyVersion, a.Parameters })
            .ToDictionaryAsync(a => a.Id, cancellationToken).ConfigureAwait(false);
        var decisionIds = orders.Where(o => o.Purpose == OrderPurpose.Entry && o.DecisionId != null).Select(o => o.DecisionId!.Value).Distinct().ToList();
        var targets = await db.Decisions.AsNoTracking().Where(d => decisionIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Targets })
            .ToDictionaryAsync(d => d.Id, d => d.Targets, cancellationToken).ConfigureAwait(false);

        var books = new Dictionary<(MarketType, string), PaperBook>();
        foreach (var trade in trades)
        {
            if (!agents.TryGetValue(trade.AgentId, out var agent))
                continue;
            var remaining = trade.RemainingSize;
            if (remaining <= 0)
            {
                _logger.LogWarning("Paper trade {TradeId} is open with no remaining size recorded; the whole size is taken as open", trade.Id);
                remaining = trade.Size;
            }

            var stop = orders.FirstOrDefault(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.StopLoss);
            var close = orders.FirstOrDefault(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.Close);
            var partial = orders.FirstOrDefault(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.PartialClose);
            var position = new PaperPosition
            {
                TradeId = trade.Id,
                AgentId = trade.AgentId,
                UserId = agent.UserId,
                Side = trade.Side,
                EntryPrice = trade.EntryPrice,
                Size = trade.Size,
                RemainingSize = remaining,
                InitialStop = TradeJson.InitialStop(trade.StopHistory) ?? trade.CurrentStop,
                Stop = stop?.StopPrice,
                StopOrderId = stop?.Id,
                HighestPrice = trade.EntryPrice,
                LowestPrice = trade.EntryPrice,
                CloseOrderId = close?.Id,
                CloseReason = close is null ? null : close.DecisionId is null ? TradeExitReason.KillSwitch : TradeExitReason.Council,
                PartialOrderId = partial?.Id,
                PartialQuantity = partial?.Quantity ?? 0m,
            };
            position.Units.AddRange(UnitsOf(orders.Where(o => o.TradeId == trade.Id && o.Purpose == OrderPurpose.TakeProfit), remaining));
            Book(books, trade.MarketType, trade.Symbol).AddPosition(position);
        }

        foreach (var order in orders.Where(o => o.Purpose == OrderPurpose.Entry))
        {
            if (!agents.TryGetValue(order.AgentId, out var agent))
                continue;
            if (order.DecisionId is not { } decisionId || order.StopPrice is not { } stop)
            {
                _logger.LogWarning("Paper entry order {OrderId} carries no decision or stop and is ignored", order.Id);
                continue;
            }

            var targetPrices = targets.TryGetValue(decisionId, out var element) ? TradeJson.Targets(element) : [];
            var fraction = PartialFraction(agent.StrategyId, agent.StrategyVersion, agent.Parameters);
            Book(books, agent.MarketType, agent.Symbol).AddEntry(new PaperEntry(order.Id, order.AgentId, agent.UserId, decisionId, agent.Timeframe,
                order.Side == OrderSide.Buy ? PositionSide.Long : PositionSide.Short, order.Type, order.Price, order.Quantity, stop, targetPrices, fraction, order.ExpiresAt));
        }

        lock (_lock)
        {
            foreach (var key in _books.Keys.Where(k => !books.ContainsKey(k)).ToList())
                RemoveBook(key);
            foreach (var (key, book) in books)
            {
                var state = BookFor(key.Item1, key.Item2);
                state.Book = book;
            }
        }

        _logger.LogInformation("Paper fill simulator follows {Symbols} symbol(s) with {Entries} pending entries and {Positions} open positions",
            books.Count, books.Values.Sum(b => b.Entries.Count), books.Values.Sum(b => b.Positions.Count));
        return books.Count;
    }

    /// <summary>The units of a position from its open take-profit rows, oldest first; whatever they do not cover trails without a target.</summary>
    internal static List<PaperUnit> UnitsOf(IEnumerable<Order> takeProfits, decimal remaining)
    {
        var units = takeProfits.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => new PaperUnit { Quantity = o.Quantity, Target = o.Price, TargetOrderId = o.Id }).ToList();
        var covered = units.Sum(u => u.Quantity);
        if (covered < remaining)
            units.Add(new PaperUnit { Quantity = remaining - covered });
        return units;
    }

    private decimal PartialFraction(string strategyId, int version, JsonElement stored)
    {
        var descriptor = _catalog.Find(strategyId, version) ?? _catalog.Find(strategyId);
        if (descriptor is null)
            return 0.5m;
        return ProtectionUnits.PartialFraction(descriptor.Resolve(StrategyParameters.Read(stored)));
    }

    private async Task ProcessTickAsync(PriceArrival arrival, CancellationToken cancellationToken)
    {
        PaperBook book;
        IReadOnlyList<PaperFill> fills;
        var at = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_books.TryGetValue((arrival.Market, arrival.Symbol), out var state))
                return;
            book = state.Book;
            fills = book.OnTick(arrival.Tick.Price, at, _options.Value.SlippageRate);
        }

        foreach (var fill in fills)
        {
            try
            {
                switch (fill)
                {
                    case EntryFilled filled:
                        await RecordEntryFillAsync(book, filled, cancellationToken).ConfigureAwait(false);
                        break;
                    case EntryExpired expired:
                        await RecordExpiryAsync(expired, cancellationToken).ConfigureAwait(false);
                        break;
                    case PositionReduced reduced:
                        await RecordReducedAsync(book, reduced, cancellationToken).ConfigureAwait(false);
                        break;
                    case PositionClosed closed:
                        await RecordCloseAsync(book, closed, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A paper fill for {Symbol} could not be recorded; the books are reloaded from the database", book.Symbol);
                await ReloadAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        lock (_lock)
        {
            if (_books.TryGetValue((book.Market, book.Symbol), out var state) && ReferenceEquals(state.Book, book) && book.IsEmpty)
                RemoveBook((book.Market, book.Symbol));
        }
    }

    private async Task RecordEntryFillAsync(PaperBook book, EntryFilled fill, CancellationToken cancellationToken)
    {
        var entry = fill.Entry;
        var options = _options.Value;
        var fee = fill.Price * entry.Quantity * (fill.IsMaker ? options.MakerFeeRate(book.Market) : options.TakerFeeRate(book.Market));
        var exitSide = entry.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
        var units = ProtectionUnits.Split(entry.Quantity, entry.Targets, entry.PartialFraction);

        Trade? trade = null;
        Order? stopOrder = null;
        var targetOrders = new List<(Order Order, ProtectionUnit Unit)>();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            trade = null;
            stopOrder = null;
            targetOrders.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == entry.OrderId, cancellationToken).ConfigureAwait(false);
            if (order is null || order.Status is not (OrderStatus.New or OrderStatus.PartiallyFilled))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            order.Status = OrderStatus.Filled;
            order.FilledQuantity = order.Quantity;
            order.AverageFillPrice = fill.Price;
            order.FilledAt = fill.At;
            order.UpdatedAt = fill.At;
            order.Fee = fee;

            var newTrade = new Trade
            {
                Id = Guid.CreateVersion7(),
                AgentId = entry.AgentId,
                EntryDecisionId = entry.DecisionId,
                Symbol = book.Symbol,
                MarketType = book.Market,
                Timeframe = entry.Timeframe,
                Side = entry.Side,
                EntryPrice = fill.Price,
                Size = entry.Quantity,
                RemainingSize = entry.Quantity,
                CurrentStop = entry.Stop,
                StopHistory = TradeJson.StopHistory(fill.At, entry.Stop),
                Targets = JsonSerializer.SerializeToElement(entry.Targets),
                Fees = fee,
                IsPaper = true,
                OpenedAt = fill.At,
            };
            db.Trades.Add(newTrade);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            order.TradeId = newTrade.Id;
            var stop = new Order
            {
                Id = Guid.CreateVersion7(),
                AgentId = entry.AgentId,
                DecisionId = entry.DecisionId,
                TradeId = newTrade.Id,
                ClientOrderId = PaperClientOrderId.For(entry.AgentId, newTrade.Id, PaperClientOrderId.StopLoss, 0),
                Purpose = OrderPurpose.StopLoss,
                Type = OrderType.StopMarket,
                Side = exitSide,
                StopPrice = entry.Stop,
                Quantity = entry.Quantity,
                Status = OrderStatus.New,
                ReduceOnly = true,
                IsPaper = true,
                CreatedAt = fill.At,
                UpdatedAt = fill.At,
            };
            db.Orders.Add(stop);
            var sequence = 0;
            foreach (var unit in units)
            {
                if (unit.Target is not { } targetPrice)
                    continue;
                var target = new Order
                {
                    Id = Guid.CreateVersion7(),
                    AgentId = entry.AgentId,
                    DecisionId = entry.DecisionId,
                    TradeId = newTrade.Id,
                    ClientOrderId = PaperClientOrderId.For(entry.AgentId, newTrade.Id, PaperClientOrderId.TakeProfit, sequence++),
                    Purpose = OrderPurpose.TakeProfit,
                    Type = OrderType.Limit,
                    Side = exitSide,
                    Price = targetPrice,
                    Quantity = unit.Quantity,
                    Status = OrderStatus.New,
                    ReduceOnly = true,
                    IsPaper = true,
                    CreatedAt = fill.At.AddTicks(sequence),
                    UpdatedAt = fill.At,
                };
                db.Orders.Add(target);
                targetOrders.Add((target, unit));
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            trade = newTrade;
            stopOrder = stop;
        }).ConfigureAwait(false);

        if (trade is null || stopOrder is null)
        {
            _logger.LogDebug("Paper entry order {OrderId} was no longer open when its fill arrived", entry.OrderId);
            return;
        }

        lock (_lock)
        {
            var position = new PaperPosition
            {
                TradeId = trade.Id,
                AgentId = entry.AgentId,
                UserId = entry.UserId,
                Side = entry.Side,
                EntryPrice = fill.Price,
                Size = entry.Quantity,
                RemainingSize = entry.Quantity,
                InitialStop = entry.Stop,
                Stop = entry.Stop,
                StopOrderId = stopOrder.Id,
                HighestPrice = fill.Price,
                LowestPrice = fill.Price,
            };
            foreach (var unit in units)
            {
                var placed = targetOrders.FirstOrDefault(t => ReferenceEquals(t.Unit, unit));
                position.Units.Add(new PaperUnit { Quantity = unit.Quantity, Target = unit.Target, TargetOrderId = placed.Order?.Id });
            }

            book.AddPosition(position);
        }

        _logger.LogInformation("Paper {Side} entry of agent {AgentId} filled {Quantity} {Symbol} at {Price} in {Units} unit(s)", entry.Side, entry.AgentId, entry.Quantity, book.Symbol, fill.Price, units.Count);
        _events.Publish(new OrderChanged(entry.AgentId, entry.UserId, fill.At, entry.OrderId, OrderStatus.Filled));
        _events.Publish(new OrderChanged(entry.AgentId, entry.UserId, fill.At, stopOrder.Id, OrderStatus.New));
        foreach (var (target, _) in targetOrders)
            _events.Publish(new OrderChanged(entry.AgentId, entry.UserId, fill.At, target.Id, OrderStatus.New));
        _events.Publish(new TradeChanged(entry.AgentId, entry.UserId, fill.At, trade.Id, IsOpen: true));
    }

    private async Task RecordExpiryAsync(EntryExpired expired, CancellationToken cancellationToken)
    {
        var entry = expired.Entry;
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Orders
            .Where(o => o.Id == entry.OrderId && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired).SetProperty(o => o.UpdatedAt, expired.At), cancellationToken)
            .ConfigureAwait(false);
        if (rows == 0)
            return;
        _logger.LogInformation("Paper entry order {OrderId} of agent {AgentId} expired unfilled", entry.OrderId, entry.AgentId);
        _events.Publish(new OrderChanged(entry.AgentId, entry.UserId, expired.At, entry.OrderId, OrderStatus.Expired));
    }

    /// <summary>A unit's target or a partial exit: the trade stays open with less quantity, its realized P&amp;L and fees accumulated (FR-EXE-06).</summary>
    private async Task RecordReducedAsync(PaperBook book, PositionReduced reduced, CancellationToken cancellationToken)
    {
        var position = reduced.Position;
        var rate = reduced.IsMaker ? _options.Value.MakerFeeRate(book.Market) : _options.Value.TakerFeeRate(book.Market);
        var fee = reduced.Price * reduced.Quantity * rate;

        Trade? trade = null;
        var changedOrders = new List<Order>();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            trade = null;
            changedOrders.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Trades.SingleOrDefaultAsync(t => t.Id == position.TradeId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.ClosedAt is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var direction = row.Side == PositionSide.Long ? 1m : -1m;
            var realized = (reduced.Price - row.EntryPrice) * direction * reduced.Quantity - fee;
            var open = row.RemainingSize > 0 ? row.RemainingSize : row.Size;
            // The first exit takes the fees paid so far (the entry's) off the realized P&L, so it is net of every fee.
            var opening = row.RealizedPnl ?? -row.Fees;
            row.RemainingSize = Math.Max(0m, open - reduced.Quantity);
            row.Fees += fee;
            row.RealizedPnl = opening + realized;

            var orders = await db.Orders
                .Where(o => o.TradeId == row.Id && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var order in orders)
            {
                if (order.Id == reduced.OrderId)
                {
                    order.Status = OrderStatus.Filled;
                    order.FilledQuantity = reduced.Quantity;
                    order.AverageFillPrice = reduced.Price;
                    order.FilledAt = reduced.At;
                    order.Fee = fee;
                    order.RealizedPnl = realized;
                }
                else if (order.Purpose == OrderPurpose.StopLoss)
                {
                    // The stop now covers what is left (FR-EXE-02 reduce-only).
                    order.Quantity = row.RemainingSize;
                }
                else
                {
                    continue;
                }

                order.UpdatedAt = reduced.At;
                changedOrders.Add(order);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            trade = row;
        }).ConfigureAwait(false);

        if (trade is null)
        {
            _logger.LogDebug("Paper trade {TradeId} was already closed when its partial exit arrived", position.TradeId);
            return;
        }

        _logger.LogInformation("Paper trade {TradeId} of agent {AgentId} reduced by {Quantity} at {Price} ({Reason}); {Remaining} left open", trade.Id, position.AgentId, reduced.Quantity, reduced.Price, reduced.Reason, trade.RemainingSize);
        foreach (var order in changedOrders)
            _events.Publish(new OrderChanged(position.AgentId, position.UserId, reduced.At, order.Id, order.Status));
        _events.Publish(new TradeChanged(position.AgentId, position.UserId, reduced.At, trade.Id, IsOpen: true));
        await CheckDailyLossAsync(position.AgentId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordCloseAsync(PaperBook book, PositionClosed closed, CancellationToken cancellationToken)
    {
        var position = closed.Position;
        var options = _options.Value;
        var rate = closed.IsMaker ? options.MakerFeeRate(book.Market) : options.TakerFeeRate(book.Market);

        Trade? trade = null;
        var changedOrders = new List<Order>();
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            trade = null;
            changedOrders.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Trades.SingleOrDefaultAsync(t => t.Id == position.TradeId, cancellationToken).ConfigureAwait(false);
            if (row is null || row.ClosedAt is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var direction = row.Side == PositionSide.Long ? 1m : -1m;
            var quantity = closed.Quantity > 0 ? closed.Quantity : (row.RemainingSize > 0 ? row.RemainingSize : row.Size);
            var fee = closed.Price * quantity * rate;
            var realized = (closed.Price - row.EntryPrice) * direction * quantity - fee;
            var highest = Math.Max(position.HighestPrice, closed.Price);
            var lowest = Math.Min(position.LowestPrice, closed.Price);
            var opening = row.RealizedPnl ?? -row.Fees;
            row.Fees += fee;
            row.RealizedPnl = opening + realized;
            row.RemainingSize = 0m;
            var gross = row.RealizedPnl.Value + row.Fees - row.Funding;
            row.ExitPrice = row.Size > 0 ? row.EntryPrice + direction * gross / row.Size : closed.Price;
            var risk = position.InitialStop is { } initialStop ? Math.Abs(row.EntryPrice - initialStop) : 0m;
            row.RMultiple = risk > 0 && row.Size > 0 ? gross / row.Size / risk : null;
            if (row.EntryPrice > 0)
            {
                row.MaxAdverseExcursion = Math.Max(0m, (row.Side == PositionSide.Long ? row.EntryPrice - lowest : highest - row.EntryPrice) / row.EntryPrice);
                row.MaxFavorableExcursion = Math.Max(0m, (row.Side == PositionSide.Long ? highest - row.EntryPrice : row.EntryPrice - lowest) / row.EntryPrice);
            }

            row.ExitReason = closed.Reason;
            row.ClosedAt = closed.At;

            var orders = await db.Orders
                .Where(o => o.TradeId == row.Id && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var order in orders)
            {
                if (order.Id == closed.OrderId)
                {
                    order.Status = OrderStatus.Filled;
                    order.FilledQuantity = quantity;
                    order.AverageFillPrice = closed.Price;
                    order.FilledAt = closed.At;
                    order.Fee = fee;
                    order.RealizedPnl = realized;
                }
                else
                {
                    order.Status = OrderStatus.Canceled;
                }

                order.UpdatedAt = closed.At;
                changedOrders.Add(order);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            trade = row;
        }).ConfigureAwait(false);

        if (trade is null)
        {
            _logger.LogDebug("Paper trade {TradeId} was already closed when its exit arrived", position.TradeId);
            return;
        }

        _logger.LogInformation("Paper trade {TradeId} of agent {AgentId} closed at {Price} ({Reason}): P&L {Pnl}", trade.Id, position.AgentId, closed.Price, closed.Reason, trade.RealizedPnl);
        foreach (var order in changedOrders)
            _events.Publish(new OrderChanged(position.AgentId, position.UserId, closed.At, order.Id, order.Status));
        _events.Publish(new TradeChanged(position.AgentId, position.UserId, closed.At, trade.Id, IsOpen: false));
        await CheckDailyLossAsync(position.AgentId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>FR-EXE-09 after every exit fill; resolved lazily because the limit reads equity through the venues, which own this simulator.</summary>
    private async Task CheckDailyLossAsync(Guid agentId, CancellationToken cancellationToken)
    {
        try
        {
            _dailyLoss ??= _services.GetRequiredService<IDailyLossLimit>();
            await _dailyLoss.CheckAsync(agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The daily loss limit of agent {AgentId} could not be checked after a paper exit", agentId);
        }
    }

    // --- Books and subscriptions ----------------------------------------------------------------------------------

    private BookState BookFor(MarketType market, string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        var key = (market, symbol);
        if (_books.TryGetValue(key, out var state))
            return state;
        state = new BookState(new PaperBook(market, symbol));
        _books[key] = state;
        state.Subscription = _streams.SubscribePrice(new MarketKey(ExchangeEnvironment.Production, market, symbol), (tick, _) =>
        {
            _ticks.Writer.TryWrite(new PriceArrival(market, symbol, tick));
            return ValueTask.CompletedTask;
        });
        _logger.LogInformation("Paper fill simulator follows {Market} {Symbol}", market, symbol);
        return state;
    }

    private void RemoveBook((MarketType Market, string Symbol) key)
    {
        if (!_books.Remove(key, out var state))
            return;
        state.Subscription?.Dispose();
        _logger.LogInformation("Paper fill simulator no longer follows {Market} {Symbol}", key.Market, key.Symbol);
    }

    private static PaperBook Book(Dictionary<(MarketType, string), PaperBook> books, MarketType market, string symbol)
    {
        var key = (market, symbol.ToUpperInvariant());
        if (!books.TryGetValue(key, out var book))
            books[key] = book = new PaperBook(market, key.Item2);
        return book;
    }

    private sealed class BookState(PaperBook book)
    {
        public PaperBook Book { get; set; } = book;

        public IDisposable? Subscription { get; set; }
    }

    private sealed record PriceArrival(MarketType Market, string Symbol, PriceTick Tick);
}
