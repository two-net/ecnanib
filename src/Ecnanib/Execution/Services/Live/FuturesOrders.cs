using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// Binance USDⓈ-M futures mechanics (FR-EXE-02, FR-EXE-11): limit or market entries in one-way mode; one reduce-only
/// stop-market conditional order for the whole remaining size (never close-position, which would flatten a position
/// the agent does not own) and one reduce-only take-profit-market conditional order per unit with a target; a stop
/// is re-placed new-before-old, so the position is never without one (NFR-REL-03). Conditional rows carry the algo
/// id and are cancelled and queried by client algo id; once triggered the actual order id takes over.
/// </summary>
internal sealed class FuturesOrders
{
    private readonly OrderRows _rows;
    private readonly TradeLedger _ledger;
    private readonly ILogger<FuturesOrders> _logger;

    public FuturesOrders(OrderRows rows, TradeLedger ledger, ILogger<FuturesOrders> logger)
    {
        _rows = rows;
        _ledger = ledger;
        _logger = logger;
    }

    public Task<SentOrder> PlaceEntryAsync(LiveAccount account, Order row, CancellationToken cancellationToken) =>
        _rows.SendAsync(row, account, ct => account.Futures!.PlaceOrderAsync(
            new OrderRequest(account.Agent.Symbol, row.Side, row.Type, row.ClientOrderId, row.Quantity, row.Price,
                TimeInForce: row.Type == OrderType.Limit ? TimeInForce.GoodTillCanceled : null), ct), cancellationToken);

    /// <summary>The stop for the whole remaining size plus one take-profit per unit with a target; returns the rows placed.</summary>
    public async Task<IReadOnlyList<Order>> ProtectAsync(LiveAccount account, Trade trade, IReadOnlyList<ProtectionUnit> units, decimal stop, CancellationToken cancellationToken)
    {
        var placed = new List<Order> { await PlaceStopAsync(account, trade, stop, units.Sum(u => u.Quantity), cancellationToken).ConfigureAwait(false) };
        placed.AddRange(await PlaceTargetsAsync(account, trade, units, cancellationToken).ConfigureAwait(false));
        _logger.LogInformation("Futures protection of trade {TradeId} of agent {AgentId} placed: {Units} unit(s), stop {Stop}", trade.Id, account.Agent.Id, units.Count, stop);
        return placed;
    }

    public async Task<Order> PlaceStopAsync(LiveAccount account, Trade trade, decimal stop, decimal quantity, CancellationToken cancellationToken)
    {
        var agent = account.Agent;
        var filters = account.Symbol.Filters;
        var isLong = trade.Side == PositionSide.Long;
        var stopPrice = isLong ? filters.RoundPriceDownToTick(stop) : filters.RoundPriceUpToTick(stop);
        var size = filters.RoundQuantityDownToStep(quantity, marketOrder: true);
        var sequence = await _ledger.CountRowsAsync(trade.Id, OrderPurpose.StopLoss, cancellationToken).ConfigureAwait(false);
        var row = _rows.New(agent, OrderPurpose.StopLoss, OrderType.StopMarket, isLong ? OrderSide.Sell : OrderSide.Buy,
            ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.StopLoss, sequence), size, null, stopPrice,
            trade.EntryDecisionId, trade.Id, reduceOnly: true);
        var request = new ConditionalOrderRequest(agent.Symbol, row.Side, OrderType.StopMarket, row.ClientOrderId, stopPrice, size,
            ReduceOnly: true, WorkingType: WorkingType.ContractPrice, PriceProtect: true);
        return (await _rows.SendConditionalAsync(row, account, ct => account.Futures!.PlaceConditionalOrderAsync(request, ct), cancellationToken).ConfigureAwait(false)).Row;
    }

    public async Task<IReadOnlyList<Order>> PlaceTargetsAsync(LiveAccount account, Trade trade, IReadOnlyList<ProtectionUnit> units, CancellationToken cancellationToken)
    {
        var agent = account.Agent;
        var filters = account.Symbol.Filters;
        var isLong = trade.Side == PositionSide.Long;
        var sequence = await _ledger.CountRowsAsync(trade.Id, OrderPurpose.TakeProfit, cancellationToken).ConfigureAwait(false);
        var placed = new List<Order>();
        foreach (var unit in units)
        {
            if (unit.Target is not { } rawTarget)
                continue;
            var target = isLong ? filters.RoundPriceUpToTick(rawTarget) : filters.RoundPriceDownToTick(rawTarget);
            var size = filters.RoundQuantityDownToStep(unit.Quantity, marketOrder: true);
            if (size <= 0)
                continue;
            var row = _rows.New(agent, OrderPurpose.TakeProfit, OrderType.TakeProfitMarket, isLong ? OrderSide.Sell : OrderSide.Buy,
                ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.TakeProfit, sequence++), size, target, target,
                trade.EntryDecisionId, trade.Id, reduceOnly: true);
            var request = new ConditionalOrderRequest(agent.Symbol, row.Side, OrderType.TakeProfitMarket, row.ClientOrderId, target, size,
                ReduceOnly: true, WorkingType: WorkingType.ContractPrice);
            placed.Add((await _rows.SendConditionalAsync(row, account, ct => account.Futures!.PlaceConditionalOrderAsync(request, ct), cancellationToken).ConfigureAwait(false)).Row);
        }

        return placed;
    }

    /// <summary>A reduce-only market exit (a close or a partial, FR-EXE-10/06).</summary>
    public Task<SentOrder> PlaceExitAsync(LiveAccount account, Order row, CancellationToken cancellationToken) =>
        _rows.SendAsync(row, account, ct => account.Futures!.PlaceOrderAsync(
            new OrderRequest(account.Agent.Symbol, row.Side, OrderType.Market, row.ClientOrderId, row.Quantity, ReduceOnly: true), ct), cancellationToken);

    /// <summary>Cancels a conditional row by client algo id (a triggered one as the actual order), or a regular row by client id; one the exchange no longer knows counts as gone.</summary>
    public async Task<IReadOnlyList<Order>> CancelAsync(LiveAccount account, Order row, CancellationToken cancellationToken)
    {
        var symbol = account.Agent.Symbol;
        try
        {
            if (IsConditional(row))
            {
                try
                {
                    await account.Futures!.CancelConditionalOrderAsync(OrderRef.ByClientId(row.ClientOrderId), cancellationToken).ConfigureAwait(false);
                }
                catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.UnknownOrder && row.ExchangeOrderId is { } actual)
                {
                    await account.Futures!.CancelOrderAsync(symbol, OrderRef.ById(actual), cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await account.Futures!.CancelOrderAsync(symbol, OrderRef.ByClientId(row.ClientOrderId), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.UnknownOrder)
        {
            _logger.LogDebug("Order {ClientOrderId} of agent {AgentId} was already gone when cancelled", row.ClientOrderId, account.Agent.Id);
        }

        var updated = await _ledger.UpdateOrderAsync(row.Id, o =>
        {
            if (o.Status.IsOpen())
                o.Status = OrderStatus.Canceled;
        }, cancellationToken).ConfigureAwait(false);
        return updated is null ? [] : [updated];
    }

    public async Task<RemoteOrder?> QueryAsync(LiveAccount account, Order row, CancellationToken cancellationToken)
    {
        try
        {
            if (IsConditional(row))
                return OrderRows.ToRemote(await account.Futures!.GetConditionalOrderAsync(OrderRef.ByClientId(row.ClientOrderId), cancellationToken).ConfigureAwait(false));
            return OrderRows.ToRemote(await account.Futures!.GetOrderAsync(account.Agent.Symbol, OrderRef.ByClientId(row.ClientOrderId), cancellationToken).ConfigureAwait(false));
        }
        catch (ExchangeException ex) when (ex.Kind == ExchangeErrorKind.UnknownOrder)
        {
            return null;
        }
    }

    public static bool IsConditional(Order row) =>
        row.ExchangeAlgoId is not null || (row.Purpose is OrderPurpose.StopLoss or OrderPurpose.TakeProfit && row.Type is OrderType.StopMarket or OrderType.TakeProfitMarket or OrderType.StopLossLimit or OrderType.TakeProfitLimit);
}
