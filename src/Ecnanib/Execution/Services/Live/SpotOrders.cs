using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// Binance Spot mechanics (FR-EXE-01, FR-AGT-12): limit or market buys; per protection unit one OCO (a limit-maker
/// take-profit and a stop-loss-limit leg whose limit sits <c>Execution:SpotStopLimitOffsetPercent</c> under the stop)
/// or a plain stop-loss-limit for a unit without target; when the symbol's algo-order headroom cannot take one order
/// per unit, one OCO at the final target covers the whole size. Protection places only the units no open row covers
/// yet (NFR-REL-02). Spot locks the quantity of an open sell, so an exit or a replacement cancels the protection it
/// needs first, tolerantly, and the caller re-protects what was freed (NFR-REL-03).
/// </summary>
internal sealed class SpotOrders
{
    private readonly OrderRows _rows;
    private readonly TradeLedger _ledger;
    private readonly IOptions<ExecutionOptions> _options;
    private readonly ILogger<SpotOrders> _logger;

    public SpotOrders(OrderRows rows, TradeLedger ledger, IOptions<ExecutionOptions> options, ILogger<SpotOrders> logger)
    {
        _rows = rows;
        _ledger = ledger;
        _options = options;
        _logger = logger;
    }

    public Task<SentOrder> PlaceEntryAsync(LiveAccount account, Order row, CancellationToken cancellationToken) =>
        _rows.SendAsync(row, account, ct => account.Spot!.PlaceOrderAsync(
            new OrderRequest(account.Agent.Symbol, row.Side, row.Type, row.ClientOrderId, row.Quantity, row.Price,
                TimeInForce: row.Type == OrderType.Limit ? TimeInForce.GoodTillCanceled : null), ct), cancellationToken);

    /// <summary>
    /// Places the protection of a long position (FR-EXE-01) for the units no open row covers yet (NFR-REL-02); returns the
    /// rows placed, or the standing ones when nothing was missing. Units beyond the headroom collapse into one at the final target.
    /// </summary>
    public async Task<IReadOnlyList<Order>> ProtectAsync(LiveAccount account, Trade trade, IReadOnlyList<ProtectionUnit> units, decimal stop, CancellationToken cancellationToken)
    {
        var agent = account.Agent;
        var filters = account.Symbol.Filters;
        var stopPrice = filters.RoundPriceDownToTick(stop);
        var stopLimit = filters.RoundPriceDownToTick(stopPrice * (1m - _options.Value.SpotStopLimitOffsetRate));
        var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        var standing = open.Where(OrderRows.IsProtective).ToList();
        // Asked for the whole position while it is already protected at this stop (a collapsed OCO, or every unit's own): nothing to
        // place. A partial set (an uncovered or freed unit) is matched unit by unit instead.
        var total = filters.RoundQuantityDownToStep(units.Sum(u => u.Quantity));
        var wholePosition = total >= filters.RoundQuantityDownToStep(TradeMath.OpenSize(trade));
        var coveredAtStop = standing.Where(o => o.Purpose == OrderPurpose.StopLoss && o.StopPrice == stopPrice).Sum(o => o.Quantity);
        var missing = wholePosition && coveredAtStop >= total ? [] : OrderRows.Missing(units, open, stopPrice, filters, isLong: true, futures: false);
        if (missing.Count == 0)
        {
            _logger.LogInformation("Trade {TradeId} of agent {AgentId}: its protection at {Stop} already stands; nothing is placed again", trade.Id, agent.Id, stopPrice);
            return standing;
        }

        if (missing.Count < units.Count)
            _logger.LogInformation("Trade {TradeId} of agent {AgentId}: {Covered} unit(s) already protected at {Stop}; only the missing ones are placed", trade.Id, agent.Id, units.Count - missing.Count, stopPrice);
        var plan = await FitHeadroomAsync(account, trade, missing, cancellationToken).ConfigureAwait(false);
        var tpSequence = await _ledger.CountRowsAsync(trade.Id, OrderPurpose.TakeProfit, cancellationToken).ConfigureAwait(false);
        var slSequence = await _ledger.CountRowsAsync(trade.Id, OrderPurpose.StopLoss, cancellationToken).ConfigureAwait(false);
        var placed = new List<Order>();
        foreach (var unit in plan)
        {
            var quantity = filters.RoundQuantityDownToStep(unit.Quantity);
            if (quantity <= 0)
                continue;
            if (unit.Target is { } rawTarget && account.Symbol.OcoAllowed)
            {
                var target = filters.RoundPriceUpToTick(rawTarget);
                var listId = ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.OcoList, tpSequence);
                var tp = _rows.New(agent, OrderPurpose.TakeProfit, OrderType.LimitMaker, OrderSide.Sell,
                    ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.TakeProfit, tpSequence), quantity, target, null,
                    trade.EntryDecisionId, trade.Id, reduceOnly: false, listClientOrderId: listId);
                var sl = _rows.New(agent, OrderPurpose.StopLoss, OrderType.StopLossLimit, OrderSide.Sell,
                    ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.StopLoss, slSequence), quantity, stopLimit, stopPrice,
                    trade.EntryDecisionId, trade.Id, reduceOnly: false, listClientOrderId: listId);
                tpSequence++;
                slSequence++;
                var request = new SpotOcoRequest(agent.Symbol, OrderSide.Sell, quantity, target, stopPrice, stopLimit, listId, tp.ClientOrderId, sl.ClientOrderId);
                var (tpRow, slRow) = await _rows.SendOcoAsync(tp, sl, account, ct => account.Spot!.PlaceOcoAsync(request, ct), cancellationToken).ConfigureAwait(false);
                placed.Add(tpRow);
                placed.Add(slRow);
            }
            else
            {
                var sl = _rows.New(agent, OrderPurpose.StopLoss, OrderType.StopLossLimit, OrderSide.Sell,
                    ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.StopLoss, slSequence), quantity, stopLimit, stopPrice,
                    trade.EntryDecisionId, trade.Id, reduceOnly: false);
                slSequence++;
                placed.Add((await _rows.SendAsync(sl, account, ct => account.Spot!.PlaceOrderAsync(
                    new OrderRequest(agent.Symbol, OrderSide.Sell, OrderType.StopLossLimit, sl.ClientOrderId, quantity, stopLimit, stopPrice, TimeInForce.GoodTillCanceled), ct), cancellationToken).ConfigureAwait(false)).Row);
            }
        }

        _logger.LogInformation("Spot protection of trade {TradeId} of agent {AgentId} placed: {Units} unit(s), stop {Stop}", trade.Id, agent.Id, plan.Count, stopPrice);
        return placed;
    }

    /// <summary>FR-EXE-01 against MAX_NUM_ALGO_ORDERS: every stop leg counts, so when the symbol cannot take one per unit the whole size takes one OCO at the final target.</summary>
    private async Task<IReadOnlyList<ProtectionUnit>> FitHeadroomAsync(LiveAccount account, Trade trade, IReadOnlyList<ProtectionUnit> units, CancellationToken cancellationToken)
    {
        if (units.Count <= 1 || account.Symbol.Filters.MaxNumAlgoOrders is not { } max)
            return units;
        var open = await account.Spot!.GetOpenOrdersAsync(account.Agent.Symbol, cancellationToken).ConfigureAwait(false);
        var algo = open.Count(o => o.Type is OrderType.StopLossLimit or OrderType.StopMarket or OrderType.TakeProfitLimit or OrderType.TakeProfitMarket);
        var headroom = max - algo;
        if (headroom >= units.Count)
            return units;
        var total = units.Sum(u => u.Quantity);
        var final = units.LastOrDefault(u => u.Target is not null)?.Target;
        _logger.LogWarning("Trade {TradeId} of agent {AgentId}: {Symbol} allows {Headroom} more stop order(s), fewer than its {Units} units; the whole size is protected as one unit", trade.Id, account.Agent.Id, account.Agent.Symbol, headroom, units.Count);
        return [new ProtectionUnit(total, final)];
    }

    /// <summary>A market sell of <paramref name="quantity"/> (a close or a partial, FR-EXE-10/06); the caller has cancelled the protection that locked it.</summary>
    public Task<SentOrder> PlaceExitAsync(LiveAccount account, Order row, CancellationToken cancellationToken) =>
        _rows.SendAsync(row, account, ct => account.Spot!.PlaceOrderAsync(
            new OrderRequest(account.Agent.Symbol, OrderSide.Sell, OrderType.Market, row.ClientOrderId, row.Quantity), ct), cancellationToken);

    /// <summary>
    /// Cancels protective groups (an OCO list or a lone stop-loss row) and reports the ones the exchange kept: a failure
    /// other than UnknownOrder is logged and the group's rows stay open, so the caller protects what was freed and never
    /// leaves a unit uncovered (NFR-REL-03).
    /// </summary>
    public async Task<(IReadOnlyList<Order> Cancelled, IReadOnlyList<Order> Kept)> CancelProtectionAsync(LiveAccount account, IReadOnlyList<Order> rows, IReadOnlyList<Order> openRows, CancellationToken cancellationToken)
    {
        var cancelled = new List<Order>();
        var kept = new List<Order>();
        foreach (var row in rows.DistinctBy(o => o.ListClientOrderId ?? o.ClientOrderId))
        {
            try
            {
                cancelled.AddRange(await CancelAsync(account, row, openRows, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Protective order {ClientOrderId} of agent {AgentId} could not be cancelled; it keeps protecting its unit at the previous stop (NFR-REL-03)", row.ClientOrderId, account.Agent.Id);
                kept.AddRange(row.ListClientOrderId is { } list ? openRows.Where(o => o.ListClientOrderId == list) : [row]);
            }
        }

        return (cancelled, kept);
    }

    /// <summary>Cancels a protective row (and its OCO sibling) or an entry; an order the exchange no longer knows counts as gone. Returns the rows marked cancelled.</summary>
    public async Task<IReadOnlyList<Order>> CancelAsync(LiveAccount account, Order row, IReadOnlyList<Order> openRows, CancellationToken cancellationToken)
    {
        var symbol = account.Agent.Symbol;
        var group = row.ListClientOrderId is { } list ? openRows.Where(o => o.ListClientOrderId == list).ToList() : [row];
        try
        {
            if (row.ListClientOrderId is { } listId)
                await account.Spot!.CancelOcoAsync(symbol, listId, cancellationToken).ConfigureAwait(false);
            else
                await account.Spot!.CancelOrderAsync(symbol, OrderRef.ByClientId(row.ClientOrderId), cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.UnknownOrder)
        {
            _logger.LogDebug("Order {ClientOrderId} of agent {AgentId} was already gone when cancelled", row.ClientOrderId, account.Agent.Id);
        }

        var cancelled = new List<Order>();
        foreach (var member in group)
        {
            var updated = await _ledger.UpdateOrderAsync(member.Id, o =>
            {
                if (o.Status.IsOpen())
                    o.Status = OrderStatus.Canceled;
            }, cancellationToken).ConfigureAwait(false);
            if (updated is not null)
                cancelled.Add(updated);
        }

        return cancelled;
    }

    public async Task<RemoteOrder?> QueryAsync(LiveAccount account, Order row, CancellationToken cancellationToken)
    {
        try
        {
            return OrderRows.ToRemote(await account.Spot!.GetOrderAsync(account.Agent.Symbol, OrderRef.ByClientId(row.ClientOrderId), cancellationToken).ConfigureAwait(false));
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.UnknownOrder)
        {
            return null;
        }
    }
}
