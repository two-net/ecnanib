using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// What a fill means for the books, whoever brings it (the user-data stream, a reconciliation read or the exchange's
/// answer to a placement): an entry fill opens or grows the trade and places its protection with the FR-EXE-05
/// retries; an exit fill reduces or closes the trade with its fee, realized P&amp;L and, at the close, the funding
/// (FR-EXE-12), re-sizes the futures stop to what is left and runs the daily loss check (FR-EXE-09); a cancellation,
/// expiry or rejection marks the row. Everything is idempotent on the row's status and filled quantity.
/// </summary>
internal sealed class FillHandler
{
    private readonly TradeLedger _ledger;
    private readonly ProtectionPlacer _placer;
    private readonly SpotOrders _spot;
    private readonly FuturesOrders _futures;
    private readonly FeeConverter _fees;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<FillHandler> _logger;
    private IDailyLossLimit? _dailyLoss;

    public FillHandler(TradeLedger ledger, ProtectionPlacer placer, SpotOrders spot, FuturesOrders futures, FeeConverter fees, IServiceProvider services, TimeProvider time, ILogger<FillHandler> logger)
    {
        _ledger = ledger;
        _placer = placer;
        _spot = spot;
        _futures = futures;
        _fees = fees;
        _services = services;
        _time = time;
        _logger = logger;
    }

    /// <summary>Applies the exchange's view of one of our rows. <paramref name="fill"/> is the fill of this very event when the stream delivered one.</summary>
    public async Task ApplyAsync(LiveAccount account, Order row, RemoteOrder remote, UserTrade? fill, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(remote);
        // FR-LOG-02: fills arrive from the stream and the reconciler, outside any evaluation, so the scope names everything itself.
        using var scope = _logger.BeginScope(agentId: account.Agent.Id, userId: account.Agent.UserId, orderId: row.Id, decisionId: row.DecisionId, tradeId: row.TradeId);
        var at = fill?.Timestamp ?? _time.GetUtcNow();

        if (remote.ExchangeOrderId is { } id && row.ExchangeOrderId != id || remote.AlgoId is { } algo && row.ExchangeAlgoId != algo)
        {
            row = await _ledger.UpdateOrderAsync(row.Id, o =>
            {
                if (remote.ExchangeOrderId is { } exchangeId)
                    o.ExchangeOrderId = exchangeId;
                if (remote.AlgoId is { } algoId)
                    o.ExchangeAlgoId = algoId;
            }, cancellationToken).ConfigureAwait(false) ?? row;
        }

        if (row.Purpose == OrderPurpose.Entry)
            await ApplyEntryAsync(account, row, remote, fill, at, cancellationToken).ConfigureAwait(false);
        else
            await ApplyExitAsync(account, row, remote, fill, at, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyEntryAsync(LiveAccount account, Order row, RemoteOrder remote, UserTrade? fill, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var agent = account.Agent;
        if (remote.FilledQuantity > row.FilledQuantity)
        {
            var fee = fill is not null
                ? await _fees.ToQuoteAsync(fill, account, cancellationToken).ConfigureAwait(false)
                : await FeesSinceAsync(account, row, row.Fee ?? 0m, cancellationToken).ConfigureAwait(false);
            var price = remote.AverageFillPrice ?? fill?.Price ?? row.Price ?? 0m;
            var (targets, fraction) = await _ledger.ProtectionPlanAsync(row, agent, cancellationToken).ConfigureAwait(false);
            var complete = remote.Status == OrderStatus.Filled || remote.Status.IsTerminal();
            var applied = await _ledger.ApplyEntryFillAsync(row.Id, agent, remote.FilledQuantity, price, fee, complete, targets, at, cancellationToken).ConfigureAwait(false);
            if (applied is not { } result)
                return;
            var (trade, opened) = result;
            _logger.LogInformation("Entry {ClientOrderId} of agent {AgentId} filled {Filled} of {Quantity} {Symbol} at {Price}; trade {TradeId} {State}",
                row.ClientOrderId, agent.Id, remote.FilledQuantity, row.Quantity, agent.Symbol, price, trade.Id, opened ? "opened" : "grown");
            if (trade.CurrentStop is not { } stop)
            {
                _logger.LogCritical("Trade {TradeId} of agent {AgentId} opened without a stop on its entry; it is closed at market (NFR-REL-03)", trade.Id, agent.Id);
                await _placer.FlattenAsync(account, trade, cancellationToken).ConfigureAwait(false);
                return;
            }

            var units = ProtectionUnits.Split(TradeMath.OpenSize(trade), targets, fraction, account.Symbol.Filters.StepSize, account.Symbol.Filters.MinQuantity);
            try
            {
                if (opened)
                    await _placer.ProtectAsync(account, trade, units, stop, cancellationToken).ConfigureAwait(false);
                else
                    await ReprotectAsync(account, trade, units, stop, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionUnavailableException ex)
            {
                _logger.LogWarning("Trade {TradeId} of agent {AgentId}: {Reason}", trade.Id, agent.Id, ex.Message);
            }

            return;
        }

        if (remote.Status.IsTerminal() && row.Status.IsOpen())
        {
            await _ledger.UpdateOrderAsync(row.Id, o => o.Status = remote.Status, cancellationToken).ConfigureAwait(false);
            _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, at, row.Id, remote.Status));
            _logger.LogInformation("Entry {ClientOrderId} of agent {AgentId} ended {Status} with {Filled} filled", row.ClientOrderId, agent.Id, remote.Status, row.FilledQuantity);
        }
    }

    private async Task ApplyExitAsync(LiveAccount account, Order row, RemoteOrder remote, UserTrade? fill, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var agent = account.Agent;
        var newlyFilled = remote.FilledQuantity - row.FilledQuantity;
        if ((fill is not null || newlyFilled > 0) && row.Status != OrderStatus.Filled && row.TradeId is { } tradeId)
        {
            var quantity = fill?.Quantity ?? newlyFilled;
            if (quantity <= 0)
                return;
            var price = fill?.Price ?? remote.AverageFillPrice ?? row.Price ?? row.StopPrice ?? 0m;
            var fee = fill is not null
                ? await _fees.ToQuoteAsync(fill, account, cancellationToken).ConfigureAwait(false)
                : await FeesSinceAsync(account, row, row.Fee ?? 0m, cancellationToken).ConfigureAwait(false);
            var complete = remote.Status == OrderStatus.Filled || row.FilledQuantity + quantity >= row.Quantity;
            var trade = await _ledger.TradeAsync(tradeId, cancellationToken).ConfigureAwait(false);
            if (trade is null || trade.ClosedAt is not null)
                return;
            var closing = complete && (row.Purpose == OrderPurpose.Close || TradeMath.OpenSize(trade) - quantity <= 0);
            var funding = closing && account.IsFutures ? await FundingAsync(account, trade, at, cancellationToken).ConfigureAwait(false) : 0m;
            var updated = await _ledger.ApplyExitFillAsync(row.Id, agent, quantity, price, fee, fill?.RealizedPnl, funding, complete, at, cancellationToken).ConfigureAwait(false);
            if (updated is null)
                return;
            if (updated.ClosedAt is not null)
            {
                _logger.LogInformation("Trade {TradeId} of agent {AgentId} closed at {Price} ({Reason}): P&L {Pnl}, fees {Fees}, funding {Funding}", updated.Id, agent.Id, price, updated.ExitReason, updated.RealizedPnl, updated.Fees, updated.Funding);
                await CancelLeftoversAsync(account, updated, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Trade {TradeId} of agent {AgentId} reduced by {Quantity} at {Price} ({Purpose}); {Remaining} left open", updated.Id, agent.Id, quantity, price, row.Purpose, updated.RemainingSize);
                if (account.IsFutures && complete)
                    await ResizeStopAsync(account, updated, cancellationToken).ConfigureAwait(false);
            }

            await CheckDailyLossAsync(agent.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (remote.Status.IsTerminal() && remote.Status != OrderStatus.Filled && row.Status.IsOpen())
        {
            await _ledger.UpdateOrderAsync(row.Id, o => o.Status = remote.Status, cancellationToken).ConfigureAwait(false);
            _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, at, row.Id, remote.Status));
            _logger.LogInformation("{Purpose} order {ClientOrderId} of agent {AgentId} ended {Status}", row.Purpose, row.ClientOrderId, agent.Id, remote.Status);
        }
    }

    /// <summary>Futures: the stop covers what is left after a partial exit (new before old, NFR-REL-03); when the new one cannot be placed the old, larger reduce-only stop stays.</summary>
    public async Task ResizeStopAsync(LiveAccount account, Trade trade, CancellationToken cancellationToken)
    {
        if (trade.CurrentStop is not { } stop)
            return;
        var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        var stops = open.Where(o => o.Purpose == OrderPurpose.StopLoss).ToList();
        var remaining = TradeMath.OpenSize(trade);
        if (remaining <= 0 || stops.Any(s => s.Quantity == remaining))
            return;
        try
        {
            await _placer.PlaceStopAsync(account, trade, stop, remaining, flattenOnFailure: false, cancellationToken).ConfigureAwait(false);
            foreach (var old in stops)
                await _futures.CancelAsync(account, old, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionUnavailableException ex)
        {
            _logger.LogWarning("The stop of trade {TradeId} of agent {AgentId} could not be re-sized; the previous stop stays: {Reason}", trade.Id, account.Agent.Id, ex.Message);
        }
    }

    /// <summary>A grown entry (a further fill): the protection follows the new size; spot re-places its OCOs, futures re-sizes the stop and places the missing targets.</summary>
    private async Task ReprotectAsync(LiveAccount account, Trade trade, IReadOnlyList<ProtectionUnit> units, decimal stop, CancellationToken cancellationToken)
    {
        var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        if (account.IsFutures)
        {
            await ResizeStopAsync(account, trade, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var row in open.Where(o => o.Purpose is OrderPurpose.StopLoss or OrderPurpose.TakeProfit).DistinctBy(o => o.ListClientOrderId ?? o.ClientOrderId))
            await _spot.CancelAsync(account, row, open, cancellationToken).ConfigureAwait(false);
        await _placer.ProtectAsync(account, trade, units, stop, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>After a close the exchange has cancelled or will cancel the siblings; whatever the rows still show open is cancelled explicitly so nothing lingers.</summary>
    private async Task CancelLeftoversAsync(LiveAccount account, Trade trade, CancellationToken cancellationToken)
    {
        var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        foreach (var row in open.DistinctBy(o => o.ListClientOrderId ?? o.ClientOrderId))
        {
            try
            {
                if (account.IsFutures)
                    await _futures.CancelAsync(account, row, cancellationToken).ConfigureAwait(false);
                else
                    await _spot.CancelAsync(account, row, open, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Leftover order {ClientOrderId} of closed trade {TradeId} could not be cancelled", row.ClientOrderId, trade.Id);
            }
        }
    }

    /// <summary>Fees of an order's fills the stream did not deliver (a reconciliation read), beyond what the row already carries.</summary>
    private async Task<decimal> FeesSinceAsync(LiveAccount account, Order row, decimal already, CancellationToken cancellationToken)
    {
        if (row.ExchangeOrderId is not { } orderId)
            return 0m;
        try
        {
            var fills = await account.Client.GetTradesAsync(account.Agent.Symbol, row.CreatedAt.AddMinutes(-1), null, 1000, cancellationToken).ConfigureAwait(false);
            var total = 0m;
            foreach (var fill in fills.Where(f => f.OrderId == orderId))
                total += await _fees.ToQuoteAsync(fill, account, cancellationToken).ConfigureAwait(false);
            return Math.Max(0m, total - already);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The fees of order {ClientOrderId} could not be read; 0 is recorded", row.ClientOrderId);
            return 0m;
        }
    }

    /// <summary>Σ FUNDING_FEE income on the symbol over the trade's life (FR-EXE-12), tolerated when the read fails.</summary>
    public async Task<decimal> FundingAsync(LiveAccount account, Trade trade, DateTimeOffset until, CancellationToken cancellationToken)
    {
        if (account.Futures is not { } futures)
            return 0m;
        try
        {
            var payments = await futures.GetFundingPaymentsAsync(account.Agent.Symbol, trade.OpenedAt, until, cancellationToken).ConfigureAwait(false);
            return payments.Sum(p => p.Amount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The funding of trade {TradeId} could not be read; 0 is recorded", trade.Id);
            return 0m;
        }
    }

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
            _logger.LogWarning(ex, "The daily loss limit of agent {AgentId} could not be checked after a live exit", agentId);
        }
    }
}
