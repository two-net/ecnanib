using Ecnanib.Agents.Abstractions;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Execution.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// FR-EXE-05: a protective order that cannot be placed is retried <c>Execution:ProtectiveOrderAttempts</c> times with
/// <c>ProtectiveOrderRetryDelayMs</c> between attempts; when it still fails the position is closed at market and the
/// failure logged at Critical. A hedge-mode refusal is a fault instead (FR-EXE-11). Every attempt places only what is
/// still missing, and an attempt whose answer was lost and could not be read back is neither retried nor flattened:
/// reconciliation settles it (NFR-REL-02). On spot, where an open sell locks its quantity, a replacement cancels the old
/// protection first, tolerantly, and never leaves a unit uncovered (NFR-REL-03).
/// </summary>
internal sealed class ProtectionPlacer
{
    private readonly SpotOrders _spot;
    private readonly FuturesOrders _futures;
    private readonly OrderRows _rows;
    private readonly TradeLedger _ledger;
    private readonly IOptions<ExecutionOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ProtectionPlacer> _logger;
    private readonly Lazy<FillHandler> _fillHandler;

    public ProtectionPlacer(SpotOrders spot, FuturesOrders futures, OrderRows rows, TradeLedger ledger, IOptions<ExecutionOptions> options, TimeProvider time, ILogger<ProtectionPlacer> logger, IServiceProvider services)
    {
        _spot = spot;
        _futures = futures;
        _rows = rows;
        _ledger = ledger;
        _options = options;
        _time = time;
        _logger = logger;
        // The fill handler places protection through this placer; a flattened position's fill goes back through it, so it is resolved lazily.
        _fillHandler = new Lazy<FillHandler>(() => (FillHandler)services.GetService(typeof(FillHandler))!);
    }

    private FillHandler _fills => _fillHandler.Value;

    /// <summary>Places the units' protection with the stop; on final failure the position is flattened and <see cref="ExecutionUnavailableException"/> says so.</summary>
    public Task<IReadOnlyList<Order>> ProtectAsync(LiveAccount account, Trade trade, IReadOnlyList<ProtectionUnit> units, decimal stop, CancellationToken cancellationToken) =>
        WithRetryAsync(account, trade, "protection", ct => account.IsFutures
            ? _futures.ProtectAsync(account, trade, units, stop, ct)
            : _spot.ProtectAsync(account, trade, units, stop, ct), cancellationToken);

    /// <summary>
    /// Futures only: a stop for <paramref name="quantity"/>. A replacement is placed before the old stop goes (NFR-REL-03), so when it
    /// cannot be placed the old stop still protects the position and the caller passes <paramref name="flattenOnFailure"/> false; a trade
    /// without any stop is flattened when its stop cannot be placed (FR-EXE-05).
    /// </summary>
    public Task<IReadOnlyList<Order>> PlaceStopAsync(LiveAccount account, Trade trade, decimal stop, decimal quantity, bool flattenOnFailure, CancellationToken cancellationToken) =>
        WithRetryAsync(account, trade, "stop", async ct => [await _futures.PlaceStopAsync(account, trade, stop, quantity, ct).ConfigureAwait(false)], cancellationToken, flattenOnFailure);

    public Task<IReadOnlyList<Order>> PlaceTargetsAsync(LiveAccount account, Trade trade, IReadOnlyList<ProtectionUnit> units, CancellationToken cancellationToken) =>
        WithRetryAsync(account, trade, "targets", ct => _futures.PlaceTargetsAsync(account, trade, units, ct), cancellationToken, flattenOnFailure: false);

    private async Task<IReadOnlyList<Order>> WithRetryAsync(LiveAccount account, Trade trade, string what, Func<CancellationToken, Task<IReadOnlyList<Order>>> place, CancellationToken cancellationToken, bool flattenOnFailure = true)
    {
        var attempts = _options.Value.ProtectiveOrderAttempts;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await place(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ExecutionFaultException)
            {
                throw;
            }
            catch (OrderOutcomeUnknownException ex)
            {
                _logger.LogWarning(ex, "The {What} of trade {TradeId} of agent {AgentId}: the answer to order {ClientOrderId} was lost and could not be read back; nothing is retried or flattened until reconciliation resolves it (NFR-REL-02)", what, trade.Id, account.Agent.Id, ex.Row.ClientOrderId);
                throw;
            }
            catch (Exception ex) when (FuturesAccountSetup.IsHedgeModeError(ex))
            {
                throw new ExecutionFaultException(FuturesAccountSetup.HedgeModeGuidance, ex);
            }
            catch (Exception ex) when (attempt < attempts)
            {
                _logger.LogWarning(ex, "Attempt {Attempt} of {Attempts} to place the {What} of trade {TradeId} of agent {AgentId} failed", attempt, attempts, what, trade.Id, account.Agent.Id);
                await Task.Delay(_options.Value.ProtectiveOrderRetryDelay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!flattenOnFailure)
                {
                    _logger.LogWarning(ex, "The {What} of trade {TradeId} of agent {AgentId} could not be placed after {Attempts} attempt(s)", what, trade.Id, account.Agent.Id, attempts);
                    throw new ExecutionUnavailableException($"The {what} could not be placed: {ex.Message}", ex);
                }

                _logger.LogCritical(ex, "The {What} of trade {TradeId} of agent {AgentId} could not be placed after {Attempts} attempt(s); the position is closed at market (FR-EXE-05)", what, trade.Id, account.Agent.Id, attempts);
                await FlattenAsync(account, trade, cancellationToken).ConfigureAwait(false);
                throw new ExecutionUnavailableException($"The {what} could not be placed after {attempts} attempt(s); the position is being closed at market.", ex);
            }
        }
    }

    /// <summary>
    /// Spot only: an open sell locks its quantity, so a replacement cannot stand beside the old protection and the old
    /// goes first. Every protective group among <paramref name="open"/> is cancelled tolerantly, the units it freed are
    /// protected at <paramref name="stop"/> with the FR-EXE-05 retries, and a group the exchange would not cancel keeps
    /// its previous protection and is reported through <see cref="ExecutionUnavailableException"/> once the rest is
    /// protected, so no unit is ever left uncovered (NFR-REL-03). The freed units are the cancelled groups' own (a stop
    /// move keeps the targets); <paramref name="units"/>, a new split for a grown position that no longer maps onto the
    /// old groups, is placed instead, less what a kept group still locks, taken off the trailing remainder. Returns the
    /// rows placed.
    /// </summary>
    public async Task<IReadOnlyList<Order>> ReplaceSpotProtectionAsync(LiveAccount account, Trade trade, IReadOnlyList<Order> open, IReadOnlyList<ProtectionUnit>? units, decimal stop, CancellationToken cancellationToken)
    {
        var (cancelled, kept) = await _spot.CancelProtectionAsync(account, open.Where(OrderRows.IsProtective).ToList(), open, cancellationToken).ConfigureAwait(false);
        var freed = units is null
            ? OrderRows.UnitsOf(cancelled, cancelled.Where(o => o.Purpose == OrderPurpose.StopLoss).Sum(o => o.Quantity))
            : kept.Count == 0 ? units : OrderRows.Reduce(units, kept.Where(o => o.Purpose == OrderPurpose.StopLoss).Sum(o => o.Quantity));
        var placed = freed.Count > 0 ? await ProtectAsync(account, trade, freed, stop, cancellationToken).ConfigureAwait(false) : [];
        if (kept.Count > 0)
            throw new ExecutionUnavailableException($"{kept.Count} protective order(s) of trade {trade.Id} could not be cancelled and keep the previous stop; the rest is protected at {stop}.");
        return placed;
    }

    /// <summary>
    /// Closes the whole remaining position at market after its protection could not be placed (FR-EXE-05). A close still
    /// pending is returned instead of a second one (NFR-REL-02). On futures the reduce-only close is placed beside the stop
    /// and the fill handler cancels what is left once the trade closes (NFR-REL-03); on spot the protection that locks the
    /// quantity is cancelled first, tolerantly.
    /// </summary>
    public async Task<Order?> FlattenAsync(LiveAccount account, Trade trade, CancellationToken cancellationToken)
    {
        try
        {
            var current = await _ledger.TradeAsync(trade.Id, cancellationToken).ConfigureAwait(false) ?? trade;
            var remaining = TradeMath.OpenSize(current);
            if (remaining <= 0)
                return null;
            var open = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
            if (open.FirstOrDefault(o => o.Purpose == OrderPurpose.Close) is { } pending)
            {
                _logger.LogInformation("Trade {TradeId} of agent {AgentId} already has close {ClientOrderId} pending; no second one is placed", trade.Id, account.Agent.Id, pending.ClientOrderId);
                return pending;
            }

            if (!account.IsFutures)
            {
                var (_, kept) = await _spot.CancelProtectionAsync(account, open.Where(OrderRows.IsProtective).ToList(), open, cancellationToken).ConfigureAwait(false);
                if (kept.Count > 0)
                    _logger.LogWarning("{Count} protective order(s) of trade {TradeId} could not be cancelled before flattening; the sell may be refused for the quantity they lock", kept.Count, trade.Id);
            }

            var agent = account.Agent;
            var closes = await _ledger.CountRowsAsync(trade.Id, OrderPurpose.Close, cancellationToken).ConfigureAwait(false);
            var row2 = _rows.New(agent, OrderPurpose.Close, OrderType.Market, current.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
                ClientOrderIds.For(ClientOrderIds.LivePrefix, agent.Id, trade.Id, ClientOrderIds.Close, closes), account.Symbol.Filters.RoundQuantityDownToStep(remaining, marketOrder: true), null, null,
                null, trade.Id, reduceOnly: account.IsFutures);
            var sent = account.IsFutures
                ? await _futures.PlaceExitAsync(account, row2, cancellationToken).ConfigureAwait(false)
                : await _spot.PlaceExitAsync(account, row2, cancellationToken).ConfigureAwait(false);
            _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, _time.GetUtcNow(), sent.Row.Id, sent.Row.Status));
            if (sent.Remote is { FilledQuantity: > 0 } remote)
                await _fills.ApplyAsync(account, sent.Row, remote, null, cancellationToken).ConfigureAwait(false);
            return sent.Row;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrderOutcomeUnknownException ex)
        {
            _logger.LogWarning(ex, "Trade {TradeId} of agent {AgentId}: the outcome of its market close is unknown; reconciliation resolves it and no second close is placed (NFR-REL-02)", trade.Id, account.Agent.Id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Trade {TradeId} of agent {AgentId} could not be closed at market after its protection failed; it is open and unprotected", trade.Id, account.Agent.Id);
            return null;
        }
    }
}
