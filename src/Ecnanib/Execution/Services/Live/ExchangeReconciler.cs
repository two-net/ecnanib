using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Execution.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Execution.Services.Live;

/// <summary>What the exchange holds for one key and market, read once per round and shared by the key's agents.</summary>
internal sealed record AccountView(IReadOnlyList<ExchangeOrder> Orders, IReadOnlyList<ConditionalOrder> Conditional, IReadOnlyList<FuturesPosition> Positions, IReadOnlyList<AssetBalance> Balances);

/// <summary>
/// FR-EXE-08: local orders and trades against the exchange at start, every <c>Exchange:ReconciliationIntervalSeconds</c>
/// and when a stream restores or a polling snapshot arrives. One read per key and market per round; each agent is
/// compared under a try-lock on its gate (an agent with an evaluation in flight waits for the next round; it reconciles
/// inline when it was interrupted). The exchange is the truth and every adoption is a Warning: a filled order is
/// processed as a fill, a vanished order marked, a vanished futures position or a sold spot holding closes the trade
/// as External with the exchange's fills, a smaller position shrinks the trade and re-sizes its stop, a trade without
/// protection is protected again, and orders or positions the system never placed are reported only.
/// </summary>
internal sealed class ExchangeReconciler : BackgroundService
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);

    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly LiveExchange _exchange;
    private readonly TradeLedger _ledger;
    private readonly FillHandler _fills;
    private readonly SpotOrders _spot;
    private readonly FuturesOrders _futures;
    private readonly ProtectionPlacer _placer;
    private readonly FeeConverter _fees;
    private readonly IServiceProvider _services;
    private readonly IOptions<ExchangeOptions> _exchangeOptions;
    private readonly IOptions<ExecutionOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ExchangeReconciler> _logger;
    private readonly SemaphoreSlim _requests = new(0, int.MaxValue);
    private IAgentGates? _gates;

    /// <summary>The scheduler's gates, resolved on first use: the scheduler's pipeline depends on the execution service, which depends on this reconciler.</summary>
    private IAgentGates Gates => _gates ??= _services.GetRequiredService<IAgentGates>();

    public ExchangeReconciler(
        IDbContextFactory<EcnanibDbContext> factory, LiveExchange exchange, TradeLedger ledger, FillHandler fills, SpotOrders spot, FuturesOrders futures, ProtectionPlacer placer,
        FeeConverter fees, IServiceProvider services, IOptions<ExchangeOptions> exchangeOptions, IOptions<ExecutionOptions> options, TimeProvider time, ILogger<ExchangeReconciler> logger)
    {
        _factory = factory;
        _exchange = exchange;
        _ledger = ledger;
        _fills = fills;
        _spot = spot;
        _futures = futures;
        _placer = placer;
        _fees = fees;
        _services = services;
        _exchangeOptions = exchangeOptions;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>A stream restored or a snapshot arrived for a key: the next round starts now.</summary>
    public void Request(string keyId, MarketType market) => _requests.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay + _options.Value.FillTrackerStartDelay, _time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RoundAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The reconciliation round failed; next attempt in {Interval}", _exchangeOptions.Value.ReconciliationInterval);
            }

            try
            {
                await _requests.WaitAsync(_exchangeOptions.Value.ReconciliationInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One round over every live agent with open rows; returns how many agents were compared.</summary>
    internal async Task<int> RoundAsync(CancellationToken cancellationToken)
    {
        var agents = await CandidatesAsync(null, cancellationToken).ConfigureAwait(false);
        var compared = 0;
        foreach (var group in agents.GroupBy(a => (a.ExchangeKeyId!.Value, a.MarketType)))
        {
            LiveAccount first;
            AccountView view;
            try
            {
                first = await _exchange.ForAsync(group.First(), null, cancellationToken).ConfigureAwait(false);
                view = await ReadAsync(first, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Key {KeyId} on {Market} could not be read for reconciliation", group.Key.Item1, group.Key.MarketType);
                continue;
            }

            foreach (var agent in group)
            {
                using var gate = Gates.TryLock(agent.Id);
                if (gate is null)
                {
                    _logger.LogDebug("Agent {AgentId} is busy; reconciled next round", agent.Id);
                    continue;
                }

                try
                {
                    var account = agent.Id == first.Agent.Id ? first : await _exchange.ForAsync(agent, first.Key, cancellationToken).ConfigureAwait(false);
                    await CompareAsync(account, view, cancellationToken).ConfigureAwait(false);
                    compared++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Agent {AgentId} could not be reconciled", agent.Id);
                }
            }
        }

        if (compared > 0)
            _logger.LogDebug("Reconciled {Agents} agent(s)", compared);
        return compared;
    }

    /// <summary>Inline reconciliation of one agent by the pipeline (NFR-REL-01); the caller holds the agent's gate.</summary>
    public async Task ReconcileAgentAsync(Agent agent, ExchangeKeyHandle? handle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Mode != AgentMode.Live)
            return;
        var account = await _exchange.ForAsync(agent, handle, cancellationToken).ConfigureAwait(false);
        var view = await ReadAsync(account, cancellationToken).ConfigureAwait(false);
        await CompareAsync(account, view, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<Agent>> CandidatesAsync(Guid? agentId, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var withOrders = db.Orders.Where(o => !o.IsPaper && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled)).Select(o => o.AgentId);
        var withTrades = db.Trades.Where(t => !t.IsPaper && t.ClosedAt == null && !t.IsUnmanaged).Select(t => t.AgentId);
        return await db.Agents.AsNoTracking()
            .Where(a => a.Mode == AgentMode.Live && a.ExchangeKeyId != null && (agentId == null || a.Id == agentId) && (withOrders.Contains(a.Id) || withTrades.Contains(a.Id)))
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AccountView> ReadAsync(LiveAccount account, CancellationToken cancellationToken)
    {
        var orders = await account.Client.GetOpenOrdersAsync(null, cancellationToken).ConfigureAwait(false);
        if (account.Futures is { } futures)
        {
            var conditional = await futures.GetOpenConditionalOrdersAsync(null, cancellationToken).ConfigureAwait(false);
            var positions = await futures.GetPositionsAsync(null, cancellationToken).ConfigureAwait(false);
            return new AccountView(orders, conditional, positions, []);
        }

        var balances = await account.Client.GetBalancesAsync(cancellationToken).ConfigureAwait(false);
        return new AccountView(orders, [], [], balances);
    }

    private async Task CompareAsync(LiveAccount account, AccountView view, CancellationToken cancellationToken)
    {
        var agent = account.Agent;
        var now = _time.GetUtcNow();
        var cutoff = now - _options.Value.ReconcileAfter;

        // Orders: a filled one becomes a fill, a vanished one is marked, a different state is adopted.
        var rows = await _ledger.OpenRowsOfAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows.Where(r => r.CreatedAt <= cutoff))
        {
            var open = view.Orders.FirstOrDefault(o => o.ClientOrderId == row.ClientOrderId);
            var conditional = view.Conditional.FirstOrDefault(o => o.ClientAlgoId == row.ClientOrderId);
            if (open is not null)
            {
                if (open.FilledQuantity > row.FilledQuantity || open.OrderId != row.ExchangeOrderId)
                    await _fills.ApplyAsync(account, row, OrderRows.ToRemote(open), null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (conditional is not null)
            {
                if (conditional.AlgoId != row.ExchangeAlgoId || conditional.ActualOrderId != row.ExchangeOrderId)
                    await _fills.ApplyAsync(account, row, OrderRows.ToRemote(conditional), null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var remote = account.IsFutures
                ? await _futures.QueryAsync(account, row, cancellationToken).ConfigureAwait(false)
                : await _spot.QueryAsync(account, row, cancellationToken).ConfigureAwait(false);
            if (remote is null)
            {
                _logger.LogWarning("Order {ClientOrderId} of agent {AgentId} is unknown to the exchange; it is recorded as cancelled (FR-EXE-08)", row.ClientOrderId, agent.Id);
                await _ledger.UpdateOrderAsync(row.Id, o => o.Status = OrderStatus.Canceled, cancellationToken).ConfigureAwait(false);
                _ledger.Publish(new OrderChanged(agent.Id, agent.UserId, now, row.Id, OrderStatus.Canceled));
                continue;
            }

            if (remote.Status != row.Status || remote.FilledQuantity != row.FilledQuantity)
            {
                _logger.LogWarning("Order {ClientOrderId} of agent {AgentId} is {Status} on the exchange ({Filled} filled) while recorded {Local}; the exchange state is adopted (FR-EXE-08)", row.ClientOrderId, agent.Id, remote.Status, remote.FilledQuantity, row.Status);
                await _fills.ApplyAsync(account, row, remote, null, cancellationToken).ConfigureAwait(false);
            }
        }

        // Trades: the exchange's position or holding is the truth.
        var trades = await _ledger.OpenTradesOfAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        foreach (var trade in trades.Where(t => !t.IsUnmanaged && t.OpenedAt <= cutoff))
        {
            var current = await _ledger.TradeAsync(trade.Id, cancellationToken).ConfigureAwait(false);
            if (current is null || current.ClosedAt is not null)
                continue;
            var local = TradeMath.OpenSize(current);
            var remaining = account.IsFutures
                ? Math.Abs(view.Positions.FirstOrDefault(p => string.Equals(p.Symbol, agent.Symbol, StringComparison.OrdinalIgnoreCase))?.Quantity ?? 0m)
                : view.Balances.FirstOrDefault(b => string.Equals(b.Asset, account.Symbol.BaseAsset, StringComparison.OrdinalIgnoreCase))?.Total ?? 0m;
            var step = account.Symbol.Filters.StepSize > 0 ? account.Symbol.Filters.StepSize : 0.00000001m;
            var openRows = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
            var protection = openRows.Where(o => o.Purpose is OrderPurpose.StopLoss or OrderPurpose.TakeProfit).ToList();

            // A position smaller than recorded while an exit of ours is pending is that exit filling; its fill is on its way and is not adopted as external too.
            if (remaining < local - step / 2 && openRows.Any(o => o.Purpose is OrderPurpose.Close or OrderPurpose.PartialClose))
                continue;

            if (remaining < step)
            {
                await CloseExternallyAsync(account, current, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (remaining < local - step / 2)
            {
                var adopted = account.Symbol.Filters.RoundQuantityDownToStep(remaining);
                _logger.LogWarning("Trade {TradeId} of agent {AgentId}: the exchange holds {Remaining} {Symbol} while {Local} is recorded; the exchange size is adopted (FR-EXE-08)", trade.Id, agent.Id, adopted, agent.Symbol, local);
                var (fees, realized, exitPrice) = await ExternalExitAsync(account, current, local - adopted, cancellationToken).ConfigureAwait(false);
                var updated = await _ledger.AdoptExternalAsync(trade.Id, agent, adopted, exitPrice, fees, realized, 0m, now, cancellationToken).ConfigureAwait(false);
                if (updated is not null && account.IsFutures)
                    await _fills.ResizeStopAsync(account, updated, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (remaining > local + step / 2)
                _logger.LogWarning("Trade {TradeId} of agent {AgentId}: the exchange holds {Remaining} {Symbol}, more than the {Local} recorded; the excess is not the agent's and stays unmanaged", trade.Id, agent.Id, remaining, agent.Symbol, local);

            // The stop is the protection: a trade without an open stop row is protected again (futures: the stop alone beside the
            // targets still there; spot: whatever OCO legs remain are cancelled and the units placed afresh).
            var stillOpen = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
            if (!stillOpen.Any(o => o.Purpose == OrderPurpose.StopLoss) && current.CurrentStop is { } stop && !stillOpen.Any(o => o.Purpose is OrderPurpose.Close))
            {
                _logger.LogWarning("Trade {TradeId} of agent {AgentId} has no stop on the exchange; it is protected again at {Stop} (FR-EXE-08, NFR-REL-03)", trade.Id, agent.Id, stop);
                try
                {
                    if (account.IsFutures)
                    {
                        await _placer.PlaceStopAsync(account, current, stop, local, flattenOnFailure: true, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var leftovers = stillOpen.Where(o => o.Purpose == OrderPurpose.TakeProfit).ToList();
                        foreach (var row in leftovers.DistinctBy(o => o.ListClientOrderId ?? o.ClientOrderId))
                            await _spot.CancelAsync(account, row, stillOpen, cancellationToken).ConfigureAwait(false);
                        // Untouched, the position is split into its units afresh; once a target or a partial took its share, what is left is one unit at the final target still ahead.
                        var targets = TradeJson.Targets(current.Targets);
                        var taken = await _ledger.CountFilledRowsAsync(trade.Id, OrderPurpose.TakeProfit, cancellationToken).ConfigureAwait(false);
                        IReadOnlyList<ProtectionUnit> units = taken == 0 && local >= current.Size
                            ? ProtectionUnits.Split(local, targets, ProtectionUnits.PartialFraction(await ParametersAsync(agent, cancellationToken).ConfigureAwait(false)), account.Symbol.Filters.StepSize, account.Symbol.Filters.MinQuantity)
                            : [new ProtectionUnit(local, taken < targets.Count ? targets[^1] : null)];
                        await _placer.ProtectAsync(account, current, units, stop, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (ExecutionUnavailableException ex)
                {
                    _logger.LogWarning("Trade {TradeId} of agent {AgentId}: {Reason}", trade.Id, agent.Id, ex.Message);
                }
            }
        }

        // Orders of ours on the symbol the system does not know: reported only.
        var known = rows.Select(r => r.ClientOrderId).ToHashSet(StringComparer.Ordinal);
        var strangers = view.Orders.Where(o => string.Equals(o.Symbol, agent.Symbol, StringComparison.OrdinalIgnoreCase) && o.ClientOrderId.StartsWith(ClientOrderIds.LivePrefix, StringComparison.Ordinal) && !known.Contains(o.ClientOrderId)).Count()
            + view.Conditional.Where(o => string.Equals(o.Symbol, agent.Symbol, StringComparison.OrdinalIgnoreCase) && o.ClientAlgoId.StartsWith(ClientOrderIds.LivePrefix, StringComparison.Ordinal) && !known.Contains(o.ClientAlgoId)).Count();
        if (strangers > 0)
            _logger.LogWarning("{Count} open order(s) on {Symbol} carry the system's prefix but match no row of agent {AgentId}; they are left alone", strangers, agent.Symbol, agent.Id);
    }

    private async Task CloseExternallyAsync(LiveAccount account, Trade trade, CancellationToken cancellationToken)
    {
        var agent = account.Agent;
        var now = _time.GetUtcNow();
        var (fees, realized, exitPrice) = await ExternalExitAsync(account, trade, TradeMath.OpenSize(trade), cancellationToken).ConfigureAwait(false);
        var funding = await _fills.FundingAsync(account, trade, now, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Trade {TradeId} of agent {AgentId} was closed on the exchange outside the system; it is recorded as External at {Price} (FR-EXE-08)", trade.Id, agent.Id, exitPrice);
        await _ledger.AdoptExternalAsync(trade.Id, agent, 0m, exitPrice, fees, realized, funding, now, cancellationToken).ConfigureAwait(false);
        var leftovers = await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false);
        foreach (var row in leftovers.DistinctBy(o => o.ListClientOrderId ?? o.ClientOrderId))
        {
            try
            {
                if (account.IsFutures)
                    await _futures.CancelAsync(account, row, cancellationToken).ConfigureAwait(false);
                else
                    await _spot.CancelAsync(account, row, leftovers, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Leftover order {ClientOrderId} of externally closed trade {TradeId} could not be cancelled", row.ClientOrderId, trade.Id);
            }
        }
    }

    /// <summary>The exit the exchange saw but the system did not place: the fills on the exit side since the trade opened that belong to none of its rows.</summary>
    private async Task<(decimal Fees, decimal Realized, decimal? ExitPrice)> ExternalExitAsync(LiveAccount account, Trade trade, decimal quantity, CancellationToken cancellationToken)
    {
        try
        {
            var fills = await account.Client.GetTradesAsync(account.Agent.Symbol, trade.OpenedAt, null, 1000, cancellationToken).ConfigureAwait(false);
            var ours = (await _ledger.OpenRowsAsync(trade.Id, cancellationToken).ConfigureAwait(false)).Select(o => o.ExchangeOrderId).Where(id => id is not null).ToHashSet();
            var exitIsSell = trade.Side == PositionSide.Long;
            var external = fills.Where(f => f.IsBuyer != exitIsSell && !ours.Contains(f.OrderId)).OrderByDescending(f => f.Timestamp).ToList();
            var taken = new List<UserTrade>();
            var left = quantity;
            foreach (var fill in external)
            {
                if (left <= 0)
                    break;
                taken.Add(fill);
                left -= fill.Quantity;
            }

            if (taken.Count == 0)
                return (0m, 0m, null);
            var fees = 0m;
            foreach (var fill in taken)
                fees += await _fees.ToQuoteAsync(fill, account, cancellationToken).ConfigureAwait(false);
            var filled = taken.Sum(f => f.Quantity);
            var exitPrice = taken.Sum(f => f.Price * f.Quantity) / filled;
            var realized = taken.Any(f => f.RealizedPnl != null) ? taken.Sum(f => f.RealizedPnl ?? 0m) - fees : TradeMath.Realized(trade, exitPrice, Math.Min(quantity, filled), fees);
            return (fees, realized, exitPrice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The external fills of trade {TradeId} could not be read; the exit is recorded without them", trade.Id);
            return (0m, 0m, null);
        }
    }

    private async Task<IReadOnlyDictionary<string, System.Text.Json.JsonElement>> ParametersAsync(Agent agent, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var stored = await db.Agents.AsNoTracking().Where(a => a.Id == agent.Id).Select(a => a.Parameters).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return StrategyParameters.Read(stored);
    }
}
