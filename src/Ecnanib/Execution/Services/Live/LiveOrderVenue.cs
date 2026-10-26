using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// The live venue: Binance Spot and USDⓈ-M futures through the user's own key (FR-EXE-01, FR-EXE-02, FR-EXE-07,
/// FR-EXE-10, FR-EXE-11). Every placement writes its row first and is idempotent on its client id; the fill tracker
/// turns the exchange's answers into trades and protection. An agent whose key no longer resolves gets
/// <see cref="ExecutionUnavailableException"/>, so a Stop reports it and the state stands.
/// </summary>
internal sealed class LiveOrderVenue : IOrderVenue
{
    private readonly LiveExchange _exchange;
    private readonly SpotOrders _spot;
    private readonly FuturesOrders _futures;
    private readonly ProtectionPlacer _placer;
    private readonly FuturesAccountSetup _setup;
    private readonly OrderRows _rows;
    private readonly TradeLedger _ledger;
    private readonly FillHandler _fills;
    private readonly LiveFillTracker _tracker;
    private readonly TimeProvider _time;
    private readonly ILogger<LiveOrderVenue> _logger;

    public LiveOrderVenue(
        LiveExchange exchange, SpotOrders spot, FuturesOrders futures, ProtectionPlacer placer, FuturesAccountSetup setup, OrderRows rows,
        TradeLedger ledger, FillHandler fills, LiveFillTracker tracker, TimeProvider time, ILogger<LiveOrderVenue> logger)
    {
        _exchange = exchange;
        _spot = spot;
        _futures = futures;
        _placer = placer;
        _setup = setup;
        _rows = rows;
        _ledger = ledger;
        _fills = fills;
        _tracker = tracker;
        _time = time;
        _logger = logger;
    }

    public AgentMode Mode => AgentMode.Live;

    public async Task<Order> PlaceEntryAsync(Agent agent, EntryPlacement placement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(placement);
        Validate(agent, placement);
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        if (account.IsFutures)
            await _setup.EnsureAsync(account, cancellationToken).ConfigureAwait(false);

        var filters = account.Symbol.Filters;
        var side = placement.Side == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;
        var price = placement.EntryType == OrderType.Limit ? filters.RoundPriceToTick(placement.Price!.Value) : (decimal?)null;
        var quantity = filters.RoundQuantityDownToStep(placement.Quantity, placement.EntryType == OrderType.Market);
        var row = _rows.New(agent, OrderPurpose.Entry, placement.EntryType, side,
            ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, placement.DecisionId, ClientOrderIds.Entry, placement.Sequence),
            quantity, price, placement.Stop, placement.DecisionId, null, reduceOnly: false, expiresAt: placement.ExpiresAt);
        SentOrder sent;
        try
        {
            sent = account.IsFutures
                ? await _futures.PlaceEntryAsync(account, row, cancellationToken).ConfigureAwait(false)
                : await _spot.PlaceEntryAsync(account, row, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (FuturesAccountSetup.IsHedgeModeError(ex))
        {
            _setup.Invalidate(agent.Id);
            throw new ExecutionFaultException(FuturesAccountSetup.HedgeModeGuidance, ex);
        }
        finally
        {
            // Also after a lost answer: a row left New is followed on the stream at once (NFR-REL-02).
            _tracker.Watch(agent);
        }

        var placed = sent.Row;
        _logger.LogInformation("Live {Type} {Side} entry placed for agent {AgentId}: {Quantity} {Symbol} at {Price} (stop {Stop}), {ClientOrderId}", placement.EntryType, placement.Side, agent.Id, quantity, agent.Symbol, price?.ToString() ?? "market", placement.Stop, placed.ClientOrderId);
        _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, _time.GetUtcNow(), placed.Id, placed.Status));
        await ApplyIfFilledAsync(account, sent, cancellationToken).ConfigureAwait(false);
        return await _ledger.OrderAsync(placed.Id, cancellationToken).ConfigureAwait(false) ?? placed;
    }

    public async Task<Order> PlaceCloseAsync(Agent agent, Trade trade, Guid? decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);
        var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        if (open.FirstOrDefault(o => o.Purpose == OrderPurpose.Close) is { } pending)
            return pending;
        var current = await _ledger.TradeAsync(trade.Id, cancellationToken).ConfigureAwait(false) ?? trade;
        var remaining = TradeMath.OpenSize(current);
        if (remaining <= 0)
            throw new InvalidOperationException("The trade has nothing left to close.");
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        var closes = await _ledger.CountRowsAsync(trade.Id, OrderPurpose.Close, cancellationToken).ConfigureAwait(false);
        var row = _rows.New(agent, OrderPurpose.Close, OrderType.Market, current.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
            ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.Close, closes),
            account.Symbol.Filters.RoundQuantityDownToStep(remaining, marketOrder: true), null, null, decisionId, trade.Id, reduceOnly: account.IsFutures);
        var placed = await ExitAsync(account, current, row, open, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Live close placed for trade {TradeId} of agent {AgentId}: {Quantity} {Symbol}", trade.Id, agent.Id, row.Quantity, agent.Symbol);
        return placed;
    }

    public async Task<Order> PlacePartialCloseAsync(Agent agent, Trade trade, decimal quantity, Guid decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);
        var current = await _ledger.TradeAsync(trade.Id, cancellationToken).ConfigureAwait(false) ?? trade;
        if (quantity <= 0 || quantity > TradeMath.OpenSize(current))
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "A partial exit takes a positive quantity below the open size.");
        var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        if (open.FirstOrDefault(o => o.Purpose == OrderPurpose.PartialClose) is { } pending)
            return pending;
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        var partials = await _ledger.CountRowsAsync(trade.Id, OrderPurpose.PartialClose, cancellationToken).ConfigureAwait(false);
        var row = _rows.New(agent, OrderPurpose.PartialClose, OrderType.Market, current.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
            ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.PartialClose, partials),
            account.Symbol.Filters.RoundQuantityDownToStep(quantity, marketOrder: true), null, null, decisionId, trade.Id, reduceOnly: account.IsFutures);
        var placed = await ExitAsync(account, current, row, open, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Live partial exit placed for trade {TradeId} of agent {AgentId}: {Quantity} {Symbol}", trade.Id, agent.Id, row.Quantity, agent.Symbol);
        return placed;
    }

    /// <summary>
    /// Spot locks the quantity of its open sells, so the protection is cancelled first (tolerantly: a group the exchange kept
    /// refuses the exit and what was freed is protected again, NFR-REL-03) and what stays after the sell is protected again; a
    /// sell whose answer was lost is not protected again, because it may have executed (NFR-REL-02). Futures exits are
    /// reduce-only beside the stop.
    /// </summary>
    private async Task<Order> ExitAsync(LiveAccount account, Trade trade, Order row, List<Order> open, CancellationToken cancellationToken)
    {
        var agent = account.Agent;
        if (account.IsFutures)
        {
            SentOrder sentExit;
            try
            {
                sentExit = await _futures.PlaceExitAsync(account, row, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _tracker.Watch(agent);
            }

            _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, _time.GetUtcNow(), sentExit.Row.Id, sentExit.Row.Status));
            await ApplyIfFilledAsync(account, sentExit, cancellationToken).ConfigureAwait(false);
            return await _ledger.OrderAsync(sentExit.Row.Id, cancellationToken).ConfigureAwait(false) ?? sentExit.Row;
        }

        var remaining = TradeMath.OpenSize(trade);
        var units = OrderRows.UnitsOf(open, remaining);
        var (cancelled, kept) = await _spot.CancelProtectionAsync(account, open.Where(OrderRows.IsProtective).ToList(), open, cancellationToken).ConfigureAwait(false);
        if (kept.Count > 0)
        {
            // The sell would be refused for the quantity the kept orders lock; the units that were freed get their protection back.
            var freed = OrderRows.UnitsOf(cancelled, cancelled.Where(o => o.Purpose == OrderPurpose.StopLoss).Sum(o => o.Quantity));
            if (freed.Count > 0 && trade.CurrentStop is { } previous)
            {
                try
                {
                    await _placer.ProtectAsync(account, trade, freed, previous, cancellationToken).ConfigureAwait(false);
                }
                catch (ExecutionUnavailableException ex)
                {
                    _logger.LogWarning("Trade {TradeId} of agent {AgentId}: {Reason}", trade.Id, agent.Id, ex.Message);
                }
            }

            throw new ExecutionUnavailableException($"The exit of trade {trade.Id} cannot free the position: {kept.Count} protective order(s) could not be cancelled; it stays protected.");
        }

        SentOrder sold;
        try
        {
            sold = await _spot.PlaceExitAsync(account, row, cancellationToken).ConfigureAwait(false);
        }
        catch (OrderOutcomeUnknownException ex)
        {
            _logger.LogWarning(ex, "The exit of trade {TradeId} of agent {AgentId} may have executed; it is not protected again until reconciliation resolves it (NFR-REL-02)", trade.Id, agent.Id);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && trade.CurrentStop is { } stop)
        {
            _logger.LogWarning(ex, "The exit of trade {TradeId} of agent {AgentId} was refused; its protection is placed again", trade.Id, agent.Id);
            await _placer.ProtectAsync(account, trade, units, stop, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _tracker.Watch(agent);
        }

        _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, _time.GetUtcNow(), sold.Row.Id, sold.Row.Status));
        var left = OrderRows.Reduce(units, row.Quantity);
        if (left.Count > 0 && trade.CurrentStop is { } currentStop)
        {
            try
            {
                await _placer.ProtectAsync(account, trade, left, currentStop, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionUnavailableException ex)
            {
                _logger.LogWarning("Trade {TradeId} of agent {AgentId}: {Reason}", trade.Id, agent.Id, ex.Message);
            }
        }

        await ApplyIfFilledAsync(account, sold, cancellationToken).ConfigureAwait(false);
        return await _ledger.OrderAsync(sold.Row.Id, cancellationToken).ConfigureAwait(false) ?? sold.Row;
    }

    /// <summary>A market order the exchange filled in its answer is recorded at once; the stream's copy of the fill is then a no-op.</summary>
    private Task ApplyIfFilledAsync(LiveAccount account, SentOrder sent, CancellationToken cancellationToken) =>
        sent.Remote is { FilledQuantity: > 0 } remote
            ? _fills.ApplyAsync(account, sent.Row, remote, null, cancellationToken)
            : Task.CompletedTask;

    public async Task<IReadOnlyList<Order>> ReplaceStopAsync(Agent agent, Trade trade, decimal stop, string reason, Guid? decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        var current = await _ledger.TradeAsync(trade.Id, cancellationToken).ConfigureAwait(false) ?? trade;
        var remaining = TradeMath.OpenSize(current);
        if (remaining <= 0)
            return [];
        var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Order> placed;
        if (account.IsFutures)
        {
            placed = await _placer.PlaceStopAsync(account, current, stop, remaining, flattenOnFailure: false, cancellationToken).ConfigureAwait(false);
            foreach (var old in open.Where(o => o.Purpose == OrderPurpose.StopLoss))
                await _futures.CancelAsync(account, old, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A kept group throws here, before the stop is recorded, so the next candle's management retries the move (NFR-REL-03).
            placed = await _placer.ReplaceSpotProtectionAsync(account, current, open, null, stop, cancellationToken).ConfigureAwait(false);
        }

        var at = _time.GetUtcNow();
        await _ledger.SetStopAsync(trade.Id, stop, reason, at, cancellationToken).ConfigureAwait(false);
        trade.CurrentStop = stop;
        _logger.LogInformation("Live stop of trade {TradeId} of agent {AgentId} moved to {Stop} ({Reason})", trade.Id, agent.Id, stop, reason);
        foreach (var order in placed)
            _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, at, order.Id, order.Status));
        _ledger.Publish(new TradeChanged(agent.Id, agent.UserId, at, trade.Id, IsOpen: true));
        return placed;
    }

    public async Task<IReadOnlyList<Order>> ReplaceTargetsAsync(Agent agent, Trade trade, IReadOnlyList<decimal> targets, Guid? decisionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            return [];
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        var current = await _ledger.TradeAsync(trade.Id, cancellationToken).ConfigureAwait(false) ?? trade;
        var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        var filters = account.Symbol.Filters;
        var isLong = current.Side == PositionSide.Long;
        var takeProfits = open.Where(o => o.Purpose == OrderPurpose.TakeProfit).OrderBy(o => o.CreatedAt).ThenBy(o => o.Id).ToList();
        // The whole desired set: the i-th open unit takes targets[i], the rest keep theirs. Placing is always asked for the whole set, so a
        // unit whose new target equals a sibling's is not mistaken for already covered (NFR-REL-02/03).
        var desired = takeProfits.Select((row, i) => new ProtectionUnit(row.Quantity, i < targets.Count ? (isLong ? filters.RoundPriceUpToTick(targets[i]) : filters.RoundPriceDownToTick(targets[i])) : row.Price ?? row.StopPrice)).ToList();
        var changed = Enumerable.Range(0, takeProfits.Count).Where(i => (takeProfits[i].Price ?? takeProfits[i].StopPrice) != desired[i].Target).ToList();
        if (changed.Count == 0)
            return [];
        var placed = new List<Order>();
        if (account.IsFutures)
        {
            // New before old (NFR-REL-03): the missing targets go on first (a conditional order is not held against the position until it
            // triggers), then every changed row that covers no desired unit any more goes.
            placed.AddRange(await _placer.PlaceTargetsAsync(account, current, desired, cancellationToken).ConfigureAwait(false));
            var (_, covering) = OrderRows.Match(desired, await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false), 0m, filters, isLong, futures: true);
            foreach (var i in changed.Where(i => !covering.Any(c => c.Id == takeProfits[i].Id)))
                await _futures.CancelAsync(account, takeProfits[i], cancellationToken).ConfigureAwait(false);
        }
        else if (current.CurrentStop is { } stop)
        {
            // Spot: only the changed rows no desired unit needs any more go, one OCO at a time and tolerantly (a group the exchange keeps
            // keeps its target, which puts its old unit back into the desired set and may make another row obsolete, hence the second
            // pass); then the whole desired set is protected again, each unit only where no row covers it.
            var kept = 0;
            var stopPrice = filters.RoundPriceDownToTick(stop);
            for (var pass = 0; pass < 2; pass++)
            {
                var openNow = pass == 0 ? open : await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
                var (_, covering) = OrderRows.Match(desired, openNow, stopPrice, filters, isLong: true, futures: false);
                var obsolete = changed.Where(i => openNow.Any(o => o.Id == takeProfits[i].Id) && !covering.Any(c => c.Id == takeProfits[i].Id)).ToList();
                var keptThisPass = 0;
                foreach (var i in obsolete)
                {
                    var row = takeProfits[i];
                    var group = openNow.Where(o => o.Id == row.Id || row.ListClientOrderId is not null && o.ListClientOrderId == row.ListClientOrderId).ToList();
                    var (_, keptRows) = await _spot.CancelProtectionAsync(account, group, openNow, cancellationToken).ConfigureAwait(false);
                    if (keptRows.Count == 0)
                        continue;
                    desired[i] = new ProtectionUnit(row.Quantity, row.Price ?? row.StopPrice);
                    keptThisPass++;
                }

                kept += keptThisPass;
                if (keptThisPass == 0)
                    break;
            }

            placed.AddRange(await _placer.ProtectAsync(account, current, desired, stop, cancellationToken).ConfigureAwait(false));
            if (kept > 0)
                throw new ExecutionUnavailableException($"{kept} take-profit order(s) of trade {trade.Id} could not be cancelled and keep their target; the rest was replaced.");
        }

        if (placed.Count == 0)
            return [];
        var updated = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        await _ledger.SetTargetsAsync(trade.Id, updated.Where(o => o.Purpose == OrderPurpose.TakeProfit).OrderBy(o => o.CreatedAt).Select(o => o.Price ?? o.StopPrice ?? 0m).ToList(), cancellationToken).ConfigureAwait(false);
        var at = _time.GetUtcNow();
        _logger.LogInformation("Live targets of trade {TradeId} of agent {AgentId} replaced: {Targets}", trade.Id, agent.Id, string.Join(", ", targets));
        foreach (var order in placed)
            _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, at, order.Id, order.Status));
        _ledger.Publish(new TradeChanged(agent.Id, agent.UserId, at, trade.Id, IsOpen: true));
        return placed;
    }

    public async Task<Order> ExpireEntryAsync(Agent agent, Order entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(entry);
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        if (account.IsFutures)
            await _futures.CancelAsync(account, entry, cancellationToken).ConfigureAwait(false);
        else
            await _spot.CancelAsync(account, entry, [entry], cancellationToken).ConfigureAwait(false);

        var remote = account.IsFutures
            ? await _futures.QueryAsync(account, entry, cancellationToken).ConfigureAwait(false)
            : await _spot.QueryAsync(account, entry, cancellationToken).ConfigureAwait(false);
        var at = _time.GetUtcNow();
        if (remote is not null && remote.FilledQuantity > 0)
        {
            await _fills.ApplyAsync(account, entry, remote, null, cancellationToken).ConfigureAwait(false);
            return await _ledger.OrderAsync(entry.Id, cancellationToken).ConfigureAwait(false) ?? entry;
        }

        var ended = await _ledger.UpdateOrderAsync(entry.Id, o =>
        {
            if (o.Status.IsOpen() || o.Status == OrderStatus.Canceled)
                o.Status = OrderStatus.Expired;
        }, cancellationToken).ConfigureAwait(false) ?? entry;
        _logger.LogInformation("Live entry {ClientOrderId} of agent {AgentId} expired at its timeout", entry.ClientOrderId, agent.Id);
        _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, at, entry.Id, ended.Status));
        return ended;
    }

    public Task<int> CancelOpenOrdersAsync(Agent agent, CancellationToken cancellationToken) =>
        CancelAsync(agent, _ => true, cancellationToken);

    public Task<int> CancelEntryOrdersAsync(Agent agent, CancellationToken cancellationToken) =>
        CancelAsync(agent, o => o.Purpose == OrderPurpose.Entry, cancellationToken);

    public Task<int> CancelProtectiveOrdersAsync(Agent agent, CancellationToken cancellationToken) =>
        CancelAsync(agent, OrderRows.IsProtective, cancellationToken);

    private async Task<int> CancelAsync(Agent agent, Func<Order, bool> which, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var open = await _ledger.OpenRowsOfAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        var chosen = open.Where(which).ToList();
        if (chosen.Count == 0)
            return 0;
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        var cancelled = 0;
        var at = _time.GetUtcNow();
        foreach (var row in chosen.DistinctBy(o => o.ListClientOrderId ?? o.ClientOrderId))
        {
            var rows = account.IsFutures
                ? await _futures.CancelAsync(account, row, cancellationToken).ConfigureAwait(false)
                : await _spot.CancelAsync(account, row, open, cancellationToken).ConfigureAwait(false);
            foreach (var done in rows)
            {
                cancelled++;
                _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, at, done.Id, done.Status));
            }
        }

        _logger.LogInformation("{Orders} live order(s) of agent {AgentId} cancelled", cancelled, agent.Id);
        return cancelled;
    }

    public async Task<int> ClosePositionsAtMarketAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var trades = await _ledger.OpenTradesOfAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        foreach (var trade in trades)
            await PlaceCloseAsync(agent, trade, null, cancellationToken).ConfigureAwait(false);
        return trades.Count;
    }

    /// <summary>Futures: the wallet balance (realized, unrealized P&amp;L excluded); spot: the quote asset plus the open positions at the last price.</summary>
    public async Task<decimal> GetEquityAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        if (account.Futures is { } futures)
            return (await futures.GetAccountSummaryAsync(cancellationToken).ConfigureAwait(false)).TotalWalletBalance;
        var balances = await account.Spot!.GetBalancesAsync(cancellationToken).ConfigureAwait(false);
        var quote = balances.FirstOrDefault(b => string.Equals(b.Asset, account.Symbol.QuoteAsset, StringComparison.OrdinalIgnoreCase))?.Total ?? 0m;
        var trades = await _ledger.OpenTradesOfAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        var held = trades.Sum(TradeMath.OpenSize);
        if (held <= 0)
            return quote;
        var price = await _exchange.PriceAsync(account.Environment, account.Market, agent.Symbol, cancellationToken).ConfigureAwait(false) ?? trades.Average(t => t.EntryPrice);
        return quote + held * price;
    }

    public async Task<decimal> GetAvailableAsync(Agent agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var account = await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        if (account.Futures is { } futures)
            return (await futures.GetAccountSummaryAsync(cancellationToken).ConfigureAwait(false)).AvailableBalance;
        var balances = await account.Spot!.GetBalancesAsync(cancellationToken).ConfigureAwait(false);
        return balances.FirstOrDefault(b => string.Equals(b.Asset, account.Symbol.QuoteAsset, StringComparison.OrdinalIgnoreCase))?.Available ?? 0m;
    }

    private static void Validate(Agent agent, EntryPlacement placement)
    {
        if (agent.Mode != AgentMode.Live)
            throw new ArgumentException("The live venue serves live agents only.", nameof(agent));
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
