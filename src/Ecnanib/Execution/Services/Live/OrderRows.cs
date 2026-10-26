using System.Runtime.ExceptionServices;
using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Configuration;
using Ecnanib.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Execution.Services.Live;

/// <summary>A live order as the exchange reports it, normalised over regular and conditional orders, for reconciliation.</summary>
internal sealed record RemoteOrder(OrderStatus Status, decimal FilledQuantity, decimal? AverageFillPrice, long? ExchangeOrderId, long? AlgoId, bool Triggered);

/// <summary>A placement's row and the exchange's answer; <see cref="Remote"/> is null when the row already existed and nothing was sent. Fills in the answer are the <see cref="FillHandler"/>'s to apply.</summary>
internal sealed record SentOrder(Order Row, RemoteOrder? Remote);

/// <summary>
/// The row-first pattern of every live placement (FR-EXE-07, NFR-REL-02): the row is written with status New, the
/// request is sent once, and the row takes the exchange's ids and status. A client id the exchange already knows is
/// adopted by reading the order back; a rejection marks the row Rejected. A lost answer (a timeout, a dropped
/// connection, a cancelled token, an unclassified error) is resolved before anyone may retry: the exchange is asked for
/// the order by its client id within <c>Execution:LostAnswerLookupSeconds</c>; found, it is adopted; unknown, the row is
/// recorded as cancelled (it never reached the exchange, and still counts toward its purpose's sequence, so a retry
/// takes a fresh id) and the failure rethrown; unreadable, the row stays New for reconciliation and
/// <see cref="OrderOutcomeUnknownException"/> tells the caller to retry nothing.
/// </summary>
internal sealed class OrderRows
{
    /// <summary>Binance processes no request whose timestamp is older than its receive window (5 s by default), so an order still unknown this long after a lost answer never arrives.</summary>
    internal static readonly TimeSpan ReadBackGrace = TimeSpan.FromSeconds(5);

    private readonly TradeLedger _ledger;
    private readonly IOptions<ExecutionOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<OrderRows> _logger;

    public OrderRows(TradeLedger ledger, IOptions<ExecutionOptions> options, TimeProvider time, ILogger<OrderRows> logger)
    {
        _ledger = ledger;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public Order New(Agent agent, OrderPurpose purpose, OrderType type, OrderSide side, string clientOrderId, decimal quantity, decimal? price, decimal? stopPrice,
        Guid? decisionId, Guid? tradeId, bool reduceOnly, DateTimeOffset? expiresAt = null, string? listClientOrderId = null)
    {
        var now = _ledger.Now;
        return new Order
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            DecisionId = decisionId,
            TradeId = tradeId,
            ClientOrderId = clientOrderId,
            ListClientOrderId = listClientOrderId,
            Purpose = purpose,
            Type = type,
            Side = side,
            Price = price,
            StopPrice = stopPrice,
            Quantity = quantity,
            Status = OrderStatus.New,
            ReduceOnly = reduceOnly,
            IsPaper = false,
            ExpiresAt = expiresAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Reserves the row, sends a regular order and records the answer's ids and open or rejected status; the row already there is returned untouched.</summary>
    public async Task<SentOrder> SendAsync(Order row, LiveAccount account, Func<CancellationToken, Task<ExchangeOrder>> send, CancellationToken cancellationToken)
    {
        using var scope = Scope(account, row);
        var (reserved, existed) = await _ledger.ReserveAsync(row, cancellationToken).ConfigureAwait(false);
        if (existed)
        {
            _logger.LogInformation("Order {ClientOrderId} of agent {AgentId} was already placed; the existing row is returned", row.ClientOrderId, row.AgentId);
            return new SentOrder(reserved, null);
        }

        ExchangeOrder placed;
        try
        {
            _logger.LogDebug("Sending {Type} {Side} order {ClientOrderId} of agent {AgentId} to the exchange", row.Type, row.Side, row.ClientOrderId, row.AgentId);
            placed = await send(cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.DuplicateClientOrderId)
        {
            placed = await account.Client.GetOrderAsync(account.Agent.Symbol, OrderRef.ByClientId(row.ClientOrderId), cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (!ex.IsTransient && ex.Kind != ExchangeErrorKind.Unknown)
        {
            await _ledger.UpdateOrderAsync(row.Id, o => o.Status = OrderStatus.Rejected, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Order {ClientOrderId} of agent {AgentId} was rejected by the exchange: {Reason}", row.ClientOrderId, row.AgentId, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            placed = await ResolveAsync(row, ex, "order", ct => account.Client.GetOrderAsync(account.Agent.Symbol, OrderRef.ByClientId(row.ClientOrderId), ct), [row.Id]).ConfigureAwait(false);
        }

        _logger.LogInformation("Order {ClientOrderId} of agent {AgentId} accepted by the exchange as {ExchangeOrderId} with status {Status}", row.ClientOrderId, row.AgentId, placed.OrderId, placed.Status);
        var updated = await _ledger.UpdateOrderAsync(row.Id, o => Apply(o, placed), cancellationToken).ConfigureAwait(false) ?? row;
        return new SentOrder(updated, ToRemote(placed));
    }

    /// <summary>The same for a futures conditional (algo) order (FR-EXE-02).</summary>
    public async Task<SentOrder> SendConditionalAsync(Order row, LiveAccount account, Func<CancellationToken, Task<ConditionalOrder>> send, CancellationToken cancellationToken)
    {
        using var scope = Scope(account, row);
        var (reserved, existed) = await _ledger.ReserveAsync(row, cancellationToken).ConfigureAwait(false);
        if (existed)
        {
            _logger.LogInformation("Conditional order {ClientOrderId} of agent {AgentId} was already placed; the existing row is returned", row.ClientOrderId, row.AgentId);
            return new SentOrder(reserved, null);
        }

        ConditionalOrder placed;
        try
        {
            _logger.LogDebug("Sending conditional {Type} {Side} order {ClientOrderId} of agent {AgentId} to the exchange", row.Type, row.Side, row.ClientOrderId, row.AgentId);
            placed = await send(cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.DuplicateClientOrderId)
        {
            placed = await account.Futures!.GetConditionalOrderAsync(OrderRef.ByClientId(row.ClientOrderId), cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (!ex.IsTransient && ex.Kind != ExchangeErrorKind.Unknown)
        {
            await _ledger.UpdateOrderAsync(row.Id, o => o.Status = OrderStatus.Rejected, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Conditional order {ClientOrderId} of agent {AgentId} was rejected by the exchange: {Reason}", row.ClientOrderId, row.AgentId, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            placed = await ResolveAsync(row, ex, "conditional order", ct => account.Futures!.GetConditionalOrderAsync(OrderRef.ByClientId(row.ClientOrderId), ct), [row.Id]).ConfigureAwait(false);
        }

        _logger.LogInformation("Conditional order {ClientOrderId} of agent {AgentId} accepted by the exchange as algo {AlgoId} with status {Status}", row.ClientOrderId, row.AgentId, placed.AlgoId, StatusOf(placed));
        var updated = await _ledger.UpdateOrderAsync(row.Id, o => Apply(o, placed), cancellationToken).ConfigureAwait(false) ?? row;
        return new SentOrder(updated, ToRemote(placed));
    }

    /// <summary>The same for a spot OCO: both legs are reserved, the list is sent once, and each leg takes its report.</summary>
    public async Task<(Order TakeProfit, Order StopLoss)> SendOcoAsync(Order takeProfit, Order stopLoss, LiveAccount account, Func<CancellationToken, Task<OcoOrderList>> send, CancellationToken cancellationToken)
    {
        using var scope = Scope(account, takeProfit);
        var (tp, tpExisted) = await _ledger.ReserveAsync(takeProfit, cancellationToken).ConfigureAwait(false);
        var (sl, slExisted) = await _ledger.ReserveAsync(stopLoss, cancellationToken).ConfigureAwait(false);
        if (tpExisted || slExisted)
        {
            _logger.LogInformation("OCO {ListClientOrderId} of agent {AgentId} was already placed; the existing rows are returned", takeProfit.ListClientOrderId, takeProfit.AgentId);
            return (tp, sl);
        }

        OcoOrderList list;
        try
        {
            _logger.LogDebug("Sending OCO {ListClientOrderId} of agent {AgentId} to the exchange", takeProfit.ListClientOrderId, takeProfit.AgentId);
            list = await send(cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.DuplicateClientOrderId)
        {
            list = await account.Spot!.GetOcoAsync(takeProfit.ListClientOrderId!, cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (!ex.IsTransient && ex.Kind != ExchangeErrorKind.Unknown)
        {
            await _ledger.UpdateOrderAsync(tp.Id, o => o.Status = OrderStatus.Rejected, cancellationToken).ConfigureAwait(false);
            await _ledger.UpdateOrderAsync(sl.Id, o => o.Status = OrderStatus.Rejected, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("OCO {ListClientOrderId} of agent {AgentId} was rejected by the exchange: {Reason}", takeProfit.ListClientOrderId, takeProfit.AgentId, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            list = await ResolveAsync(takeProfit, ex, "OCO", ct => account.Spot!.GetOcoAsync(takeProfit.ListClientOrderId!, ct), [tp.Id, sl.Id]).ConfigureAwait(false);
        }

        _logger.LogInformation("OCO {ListClientOrderId} of agent {AgentId} accepted by the exchange as list {OrderListId} with status {Status}", takeProfit.ListClientOrderId, takeProfit.AgentId, list.OrderListId, list.Status);
        var tpReport = list.OrderReports?.FirstOrDefault(o => o.ClientOrderId == tp.ClientOrderId);
        var slReport = list.OrderReports?.FirstOrDefault(o => o.ClientOrderId == sl.ClientOrderId);
        var tpRef = list.Orders.FirstOrDefault(o => o.ClientOrderId == tp.ClientOrderId);
        var slRef = list.Orders.FirstOrDefault(o => o.ClientOrderId == sl.ClientOrderId);
        tp = await _ledger.UpdateOrderAsync(tp.Id, o =>
        {
            o.OrderListId = list.OrderListId;
            if (tpReport is not null)
                Apply(o, tpReport);
            else if (tpRef.OrderId is { } id)
                o.ExchangeOrderId = id;
        }, cancellationToken).ConfigureAwait(false) ?? tp;
        sl = await _ledger.UpdateOrderAsync(sl.Id, o =>
        {
            o.OrderListId = list.OrderListId;
            if (slReport is not null)
                Apply(o, slReport);
            else if (slRef.OrderId is { } id)
                o.ExchangeOrderId = id;
        }, cancellationToken).ConfigureAwait(false) ?? sl;
        return (tp, sl);
    }

    /// <summary>
    /// NFR-REL-02: the answer to a request was lost, so before any retry the exchange is asked whether it has the order. The
    /// lookup runs on its own bounded token, because the caller's may be the very token whose cancellation lost the answer.
    /// </summary>
    private async Task<T> ResolveAsync<T>(Order row, Exception failure, string what, Func<CancellationToken, Task<T>> readBack, IReadOnlyList<Guid> rowIds)
    {
        using var bounded = new CancellationTokenSource(_options.Value.LostAnswerLookup + ReadBackGrace, _time);
        T found;
        try
        {
            found = await ReadBackAsync(row, what, readBack, bounded.Token).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.UnknownOrder)
        {
            foreach (var id in rowIds)
            {
                await _ledger.UpdateOrderAsync(id, o =>
                {
                    if (o.Status.IsOpen())
                        o.Status = OrderStatus.Canceled;
                }, bounded.Token).ConfigureAwait(false);
            }

            _logger.LogWarning(failure, "The answer to {What} {ClientOrderId} of agent {AgentId} was lost and the exchange does not know it; the row is recorded as cancelled and a retry takes a fresh id (NFR-REL-02)", what, row.ClientOrderId, row.AgentId);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The answer to {What} {ClientOrderId} of agent {AgentId} was lost ({Failure}) and it could not be read back either; the row stays New for reconciliation and nothing is retried (NFR-REL-02)", what, row.ClientOrderId, row.AgentId, failure.Message);
            throw new OrderOutcomeUnknownException(row, $"The answer to {what} {row.ClientOrderId} was lost ({failure.Message}) and it could not be read back ({ex.Message}); reconciliation will resolve it.", failure);
        }

        _logger.LogWarning(failure, "The answer to {What} {ClientOrderId} of agent {AgentId} was lost; the exchange has it and it is adopted (NFR-REL-02)", what, row.ClientOrderId, row.AgentId);
        return found;
    }

    /// <summary>An order unknown right after a lost answer may still be in flight; it is asked for again once the exchange's receive window has passed, after which an unknown order never arrives.</summary>
    private async Task<T> ReadBackAsync<T>(Order row, string what, Func<CancellationToken, Task<T>> readBack, CancellationToken cancellationToken)
    {
        try
        {
            return await readBack(cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.UnknownOrder)
        {
            _logger.LogInformation("The exchange does not know {What} {ClientOrderId} of agent {AgentId} yet; the request may still be in flight, so it is asked again in {Grace}", what, row.ClientOrderId, row.AgentId, ReadBackGrace);
            await Task.Delay(ReadBackGrace, _time, cancellationToken).ConfigureAwait(false);
            return await readBack(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>FR-LOG-02: the placement's logs, and the exchange client's, name the agent, its user, the row and what it belongs to.</summary>
    private IDisposable? Scope(LiveAccount account, Order row) =>
        _logger.BeginScope(agentId: account.Agent.Id, userId: account.Agent.UserId, orderId: row.Id, decisionId: row.DecisionId, tradeId: row.TradeId);

    /// <summary>The ids and a non-fill status; fills are the fill handler's, which compares the exchange's filled quantity with the row's.</summary>
    public static void Apply(Order row, ExchangeOrder placed)
    {
        row.ExchangeOrderId = placed.OrderId;
        row.OrderListId ??= placed.OrderListId;
        if (placed.Status is not (OrderStatus.Filled or OrderStatus.PartiallyFilled) && row.Status.IsOpen())
            row.Status = placed.Status;
    }

    public static void Apply(Order row, ConditionalOrder placed)
    {
        row.ExchangeAlgoId = placed.AlgoId;
        if (placed.ActualOrderId is { } actual)
            row.ExchangeOrderId = actual;
        var status = StatusOf(placed);
        if (status is not (OrderStatus.Filled or OrderStatus.PartiallyFilled) && row.Status.IsOpen())
            row.Status = status;
    }

    /// <summary>A conditional order is open until it finished; a finished one filled when it has a filled quantity, else it was cancelled in the matching engine.</summary>
    public static OrderStatus StatusOf(ConditionalOrder order) => order.Status switch
    {
        ConditionalOrderStatus.New or ConditionalOrderStatus.Triggering or ConditionalOrderStatus.Triggered => OrderStatus.New,
        ConditionalOrderStatus.Finished => order.FilledQuantity is > 0 ? OrderStatus.Filled : OrderStatus.Canceled,
        ConditionalOrderStatus.Canceled => OrderStatus.Canceled,
        ConditionalOrderStatus.Rejected => OrderStatus.Rejected,
        ConditionalOrderStatus.Expired => OrderStatus.Expired,
        _ => OrderStatus.Other,
    };

    public static RemoteOrder ToRemote(ExchangeOrder order) => new(order.Status, order.FilledQuantity, order.AverageFillPrice, order.OrderId, null, false);

    public static RemoteOrder ToRemote(ConditionalOrder order) =>
        new(StatusOf(order), order.FilledQuantity ?? 0m, order.AverageFillPrice, order.ActualOrderId, order.AlgoId, order.Status is ConditionalOrderStatus.Triggered or ConditionalOrderStatus.Triggering or ConditionalOrderStatus.Finished);

    public static bool IsProtective(Order row) => row.Purpose is OrderPurpose.StopLoss or OrderPurpose.TakeProfit;

    /// <summary>
    /// The units no open row protects yet, so a retry or a re-protection places only what is missing (NFR-REL-02). On
    /// futures a unit with a target is covered by an open take-profit row of its quantity at its target (units without
    /// target have nothing to place there; the stop is the caller's); on spot a unit with a target is covered by an OCO
    /// whose take-profit leg has its quantity and target and whose stop leg sits at <paramref name="stopPrice"/>, a unit
    /// without target by a lone stop-loss row of its quantity at <paramref name="stopPrice"/>. Each row covers one unit.
    /// </summary>
    public static IReadOnlyList<ProtectionUnit> Missing(IReadOnlyList<ProtectionUnit> units, IReadOnlyList<Order> open, decimal stopPrice, SymbolFilters filters, bool isLong, bool futures) =>
        Match(units, open, stopPrice, filters, isLong, futures).Missing;

    /// <summary>
    /// <see cref="Missing"/> plus the open rows that cover a unit (an OCO's two legs together). Callers pass the trade's
    /// whole desired set, never one unit of it: a unit a sibling's row happens to equal counts once, because every row
    /// covers one unit, so the sibling keeps its row and the unit without one is the missing one.
    /// </summary>
    public static (IReadOnlyList<ProtectionUnit> Missing, IReadOnlyList<Order> Covering) Match(IReadOnlyList<ProtectionUnit> units, IReadOnlyList<Order> open, decimal stopPrice, SymbolFilters filters, bool isLong, bool futures)
    {
        var free = open.Where(o => o.Status.IsOpen()).ToList();
        var missing = new List<ProtectionUnit>();
        var covering = new List<Order>();
        foreach (var unit in units)
        {
            var quantity = filters.RoundQuantityDownToStep(unit.Quantity, marketOrder: futures);
            if (quantity <= 0)
                continue;
            Order? cover = null;
            if (unit.Target is { } rawTarget)
            {
                var target = isLong ? filters.RoundPriceUpToTick(rawTarget) : filters.RoundPriceDownToTick(rawTarget);
                cover = free.FirstOrDefault(o => o.Purpose == OrderPurpose.TakeProfit && o.Quantity == quantity && (o.Price ?? o.StopPrice) == target
                    && (futures || o.ListClientOrderId is { } list && free.Any(s => s.Purpose == OrderPurpose.StopLoss && s.ListClientOrderId == list && s.StopPrice == stopPrice)));
            }
            else if (futures)
            {
                continue;
            }
            else
            {
                cover = free.FirstOrDefault(o => o.Purpose == OrderPurpose.StopLoss && o.ListClientOrderId is null && o.Quantity == quantity && o.StopPrice == stopPrice);
            }

            if (cover is null)
            {
                missing.Add(unit);
                continue;
            }

            free.Remove(cover);
            covering.Add(cover);
            if (cover.ListClientOrderId is { } covered)
            {
                covering.AddRange(free.Where(o => o.ListClientOrderId == covered));
                free.RemoveAll(o => o.ListClientOrderId == covered);
            }
        }

        return (missing, covering);
    }

    /// <summary>The units a trade is protected with, from its open rows: one per take-profit (an OCO or a reduce-only target), the rest as one unit without target.</summary>
    public static IReadOnlyList<ProtectionUnit> UnitsOf(IReadOnlyList<Order> openRows, decimal remaining)
    {
        var units = openRows.Where(o => o.Purpose == OrderPurpose.TakeProfit).OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => new ProtectionUnit(o.Quantity, o.Price ?? o.StopPrice)).ToList();
        var covered = units.Sum(u => u.Quantity);
        if (covered < remaining)
            units.Add(new ProtectionUnit(remaining - covered, null));
        return units;
    }

    /// <summary>Takes an exit off the units from the last one (the trailing remainder) backwards.</summary>
    public static IReadOnlyList<ProtectionUnit> Reduce(IReadOnlyList<ProtectionUnit> units, decimal quantity)
    {
        var result = units.ToList();
        var left = quantity;
        for (var i = result.Count - 1; i >= 0 && left > 0; i--)
        {
            var taken = Math.Min(result[i].Quantity, left);
            left -= taken;
            var rest = result[i].Quantity - taken;
            if (rest <= 0)
                result.RemoveAt(i);
            else
                result[i] = result[i] with { Quantity = rest };
        }

        return result;
    }
}
