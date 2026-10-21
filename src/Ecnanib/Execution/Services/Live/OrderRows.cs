using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Execution.Services.Live;

/// <summary>A live order as the exchange reports it, normalised over regular and conditional orders, for reconciliation.</summary>
internal sealed record RemoteOrder(OrderStatus Status, decimal FilledQuantity, decimal? AverageFillPrice, long? ExchangeOrderId, long? AlgoId, bool Triggered);

/// <summary>A placement's row and the exchange's answer; <see cref="Remote"/> is null when the row already existed and nothing was sent. Fills in the answer are the <see cref="FillHandler"/>'s to apply.</summary>
internal sealed record SentOrder(Order Row, RemoteOrder? Remote);

/// <summary>
/// The row-first pattern of every live placement (FR-EXE-07, NFR-REL-02): the row is written with status New, the
/// request is sent once, and the row takes the exchange's ids and status. A client id the exchange already knows is
/// adopted by reading the order back; a rejection marks the row Rejected; a transient failure leaves the row New for
/// reconciliation, because the exchange may have accepted the order after all.
/// </summary>
internal sealed class OrderRows
{
    private readonly TradeLedger _ledger;
    private readonly ILogger<OrderRows> _logger;

    public OrderRows(TradeLedger ledger, ILogger<OrderRows> logger)
    {
        _ledger = ledger;
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
        var (reserved, existed) = await _ledger.ReserveAsync(row, cancellationToken).ConfigureAwait(false);
        if (existed)
        {
            _logger.LogInformation("Order {ClientOrderId} of agent {AgentId} was already placed; the existing row is returned", row.ClientOrderId, row.AgentId);
            return new SentOrder(reserved, null);
        }

        ExchangeOrder placed;
        try
        {
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

        var updated = await _ledger.UpdateOrderAsync(row.Id, o => Apply(o, placed), cancellationToken).ConfigureAwait(false) ?? row;
        return new SentOrder(updated, ToRemote(placed));
    }

    /// <summary>The same for a futures conditional (algo) order (FR-EXE-02).</summary>
    public async Task<SentOrder> SendConditionalAsync(Order row, LiveAccount account, Func<CancellationToken, Task<ConditionalOrder>> send, CancellationToken cancellationToken)
    {
        var (reserved, existed) = await _ledger.ReserveAsync(row, cancellationToken).ConfigureAwait(false);
        if (existed)
        {
            _logger.LogInformation("Conditional order {ClientOrderId} of agent {AgentId} was already placed; the existing row is returned", row.ClientOrderId, row.AgentId);
            return new SentOrder(reserved, null);
        }

        ConditionalOrder placed;
        try
        {
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

        var updated = await _ledger.UpdateOrderAsync(row.Id, o => Apply(o, placed), cancellationToken).ConfigureAwait(false) ?? row;
        return new SentOrder(updated, ToRemote(placed));
    }

    /// <summary>The same for a spot OCO: both legs are reserved, the list is sent once, and each leg takes its report.</summary>
    public async Task<(Order TakeProfit, Order StopLoss)> SendOcoAsync(Order takeProfit, Order stopLoss, LiveAccount account, Func<CancellationToken, Task<OcoOrderList>> send, CancellationToken cancellationToken)
    {
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
