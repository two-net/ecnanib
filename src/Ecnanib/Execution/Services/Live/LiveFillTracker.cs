using System.Collections.Concurrent;
using System.Threading.Channels;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Ecnanib.Exchange.Configuration;
using Ecnanib.Execution.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecnanib.Execution.Services.Live;

/// <summary>
/// Follows the user-data stream of every key and market with live agents that have open orders or trades (EX-03,
/// FR-EXE-01/02/12): order, OCO and conditional-order updates are matched to the rows by client id and handed to the
/// <see cref="FillHandler"/>; a snapshot or a restored stream asks the reconciler to compare (FR-EXE-08). Events are
/// queued per key and handled one at a time. Subscriptions are reviewed at start, after every placement
/// (<see cref="Watch"/>) and every reconciliation interval: a key is re-resolved each round, so rotated credentials
/// and a stopped session re-subscribe, and a key with nothing open is released.
/// </summary>
internal sealed class LiveFillTracker : BackgroundService
{
    private readonly IDbContextFactory<EcnanibDbContext> _factory;
    private readonly LiveExchange _exchange;
    private readonly TradeLedger _ledger;
    private readonly FillHandler _fills;
    private readonly FuturesAccountSetup _setup;
    private readonly ExchangeReconciler _reconciler;
    private readonly IOptions<ExchangeOptions> _exchangeOptions;
    private readonly IOptions<ExecutionOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LiveFillTracker> _logger;

    private readonly ConcurrentDictionary<(string KeyId, MarketType Market), Subscription> _subscriptions = new();
    private readonly SemaphoreSlim _refresh = new(0, int.MaxValue);
    private readonly Channel<UserDataEvent> _events = Channel.CreateUnbounded<UserDataEvent>(new UnboundedChannelOptions { SingleReader = true });

    public LiveFillTracker(
        IDbContextFactory<EcnanibDbContext> factory, LiveExchange exchange, TradeLedger ledger, FillHandler fills, FuturesAccountSetup setup, ExchangeReconciler reconciler,
        IOptions<ExchangeOptions> exchangeOptions, IOptions<ExecutionOptions> options, TimeProvider time, ILogger<LiveFillTracker> logger)
    {
        _factory = factory;
        _exchange = exchange;
        _ledger = ledger;
        _fills = fills;
        _setup = setup;
        _reconciler = reconciler;
        _exchangeOptions = exchangeOptions;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public int SubscriptionCount => _subscriptions.Count;

    /// <summary>An agent just placed something: review the subscriptions now.</summary>
    public void Watch(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _refresh.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(_options.Value.FillTrackerStartDelay, stoppingToken).ConfigureAwait(false))
            return;
        var loops = new[] { RefreshLoopAsync(stoppingToken), ProcessLoopAsync(stoppingToken) };
        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _events.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var subscription in _subscriptions.Values)
            subscription.Handle.Dispose();
        _subscriptions.Clear();
    }

    private async Task RefreshLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The fill tracker could not review its subscriptions; next attempt in {Interval}", _exchangeOptions.Value.ReconciliationInterval);
            }

            try
            {
                await _refresh.WaitAsync(_exchangeOptions.Value.ReconciliationInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One review: every (key, market) with live agents that have open rows is followed, the rest released; a rotated key or a stopped session is re-subscribed.</summary>
    internal async Task<int> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        List<Agent> agents;
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var withOrders = db.Orders.Where(o => !o.IsPaper && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled)).Select(o => o.AgentId);
            var withTrades = db.Trades.Where(t => !t.IsPaper && t.ClosedAt == null && !t.IsUnmanaged).Select(t => t.AgentId);
            agents = await db.Agents.AsNoTracking()
                .Where(a => a.Mode == AgentMode.Live && a.ExchangeKeyId != null && (withOrders.Contains(a.Id) || withTrades.Contains(a.Id)))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var wanted = new HashSet<(string KeyId, MarketType Market)>();
        foreach (var group in agents.GroupBy(a => (KeyId: a.ExchangeKeyId!.Value.ToString("D"), Market: a.MarketType)))
        {
            var agent = group.First();
            ExchangeKeyHandle handle;
            try
            {
                handle = await _exchange.ResolveAsync(agent, cancellationToken).ConfigureAwait(false);
            }
            catch (ExecutionUnavailableException ex)
            {
                _logger.LogWarning("The user-data stream for key {KeyId} on {Market} cannot be followed: {Reason}", group.Key.KeyId, group.Key.Market, ex.Message);
                continue;
            }

            wanted.Add((handle.KeyId, group.Key.Market));
            if (_subscriptions.TryGetValue((handle.KeyId, group.Key.Market), out var existing))
            {
                var stopped = existing.Client.UserDataStatus.Health == StreamHealth.Stopped;
                var rotated = !string.Equals(existing.ApiKey, handle.Credentials.ApiKey, StringComparison.Ordinal);
                if (!stopped && !rotated)
                    continue;
                existing.Handle.Dispose();
                _subscriptions.TryRemove((handle.KeyId, group.Key.Market), out _);
                _logger.LogInformation("The user-data stream for key {KeyId} on {Market} is subscribed again ({Why})", handle.KeyId, group.Key.Market, rotated ? "credentials rotated" : "session stopped");
            }

            var client = group.Key.Market == MarketType.Spot
                ? (IExchangeAccountClient)(await _exchange.ForAsync(agent, handle, cancellationToken).ConfigureAwait(false)).Spot!
                : (await _exchange.ForAsync(agent, handle, cancellationToken).ConfigureAwait(false)).Futures!;
            var subscription = client.SubscribeUserData((e, _) =>
            {
                _events.Writer.TryWrite(e);
                return ValueTask.CompletedTask;
            });
            _subscriptions[(handle.KeyId, group.Key.Market)] = new Subscription(subscription, client, handle.Credentials.ApiKey);
            _logger.LogInformation("The user-data stream for key {KeyId} on {Market} is followed for {Agents} agent(s)", handle.KeyId, group.Key.Market, group.Count());
        }

        foreach (var key in _subscriptions.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            if (_subscriptions.TryRemove(key, out var released))
            {
                released.Handle.Dispose();
                _logger.LogInformation("The user-data stream for key {KeyId} on {Market} is released: nothing open", key.KeyId, key.Market);
            }
        }

        return _subscriptions.Count;
    }

    private async Task ProcessLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var e in _events.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(e, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A user-data event for key {KeyId} on {Market} could not be handled", e.KeyId, e.Market);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One event; internal so tests can push events without a stream.</summary>
    internal async Task HandleAsync(UserDataEvent e, CancellationToken cancellationToken)
    {
        switch (e)
        {
            case UserDataEvent.OrderUpdate update:
                await OnOrderAsync(update, cancellationToken).ConfigureAwait(false);
                break;
            case UserDataEvent.ConditionalOrderUpdate conditional:
                await OnConditionalAsync(conditional, cancellationToken).ConfigureAwait(false);
                break;
            case UserDataEvent.OcoUpdate oco:
                foreach (var leg in oco.List.Orders)
                {
                    if (leg.ClientOrderId is { } clientId && await _ledger.OrderByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false) is { } row && row.OrderListId is null)
                        await _ledger.UpdateOrderAsync(row.Id, o => o.OrderListId = oco.List.OrderListId, cancellationToken).ConfigureAwait(false);
                }

                break;
            case UserDataEvent.LeverageUpdate leverage:
                foreach (var agentId in await AgentsOnAsync(leverage.KeyId, leverage.Market, leverage.Symbol, cancellationToken).ConfigureAwait(false))
                    _setup.Invalidate(agentId);
                break;
            case UserDataEvent.Snapshot or UserDataEvent.StreamRestored:
                _reconciler.Request(e.KeyId, e.Market);
                break;
        }
    }

    private async Task OnOrderAsync(UserDataEvent.OrderUpdate update, CancellationToken cancellationToken)
    {
        var order = update.Order;
        var row = await _ledger.OrderByClientIdAsync(order.ClientOrderId, cancellationToken).ConfigureAwait(false)
            ?? await _ledger.OrderByExchangeIdAsync(order.OrderId, order.Symbol, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogDebug("Order update for {ClientOrderId} on {Symbol} matches no row of ours", order.ClientOrderId, order.Symbol);
            return;
        }

        var account = await AccountAsync(row.AgentId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return;
        await _fills.ApplyAsync(account, row, OrderRows.ToRemote(order), update.Fill, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnConditionalAsync(UserDataEvent.ConditionalOrderUpdate update, CancellationToken cancellationToken)
    {
        var order = update.Order;
        var row = await _ledger.OrderByClientIdAsync(order.ClientAlgoId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            _logger.LogDebug("Conditional order update for {ClientAlgoId} on {Symbol} matches no row of ours", order.ClientAlgoId, order.Symbol);
            return;
        }

        var account = await AccountAsync(row.AgentId, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return;
        var remote = OrderRows.ToRemote(order);
        if (order.Status == ConditionalOrderStatus.Finished && order.FilledQuantity is > 0 && row.Status != OrderStatus.Filled)
        {
            // The actual order's fills carry the fee and realized P&L; read them when the regular update did not arrive first.
            UserTrade? fill = null;
            if (order.ActualOrderId is { } actual)
            {
                try
                {
                    var fills = await account.Client.GetTradesAsync(account.Agent.Symbol, row.CreatedAt.AddMinutes(-1), null, 1000, cancellationToken).ConfigureAwait(false);
                    var mine = fills.Where(f => f.OrderId == actual).ToList();
                    if (mine.Count > 0)
                        fill = new UserTrade(order.Symbol, mine[^1].TradeId, actual, mine.Sum(f => f.Price * f.Quantity) / mine.Sum(f => f.Quantity), mine.Sum(f => f.Quantity), mine.Sum(f => f.QuoteQuantity),
                            mine.Sum(f => f.Fee), mine[0].FeeAsset, mine[0].IsBuyer, mine[0].IsMaker, mine.Any(f => f.RealizedPnl != null) ? mine.Sum(f => f.RealizedPnl ?? 0m) : null, update.Timestamp);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "The fills of triggered order {ClientAlgoId} could not be read; the fee is recorded as 0", order.ClientAlgoId);
                }
            }

            await _fills.ApplyAsync(account, row, remote, fill, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _fills.ApplyAsync(account, row, remote, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LiveAccount?> AccountAsync(Guid agentId, CancellationToken cancellationToken)
    {
        Agent? agent;
        await using (var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
            return null;
        try
        {
            return await _exchange.ForAsync(agent, null, cancellationToken).ConfigureAwait(false);
        }
        catch (ExecutionUnavailableException ex)
        {
            _logger.LogWarning("A fill of agent {AgentId} cannot be handled: {Reason}", agentId, ex.Message);
            return null;
        }
    }

    private async Task<List<Guid>> AgentsOnAsync(string keyId, MarketType market, string symbol, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(keyId, out var key))
            return [];
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Agents.AsNoTracking().Where(a => a.ExchangeKeyId == key && a.MarketType == market && a.Symbol == symbol).Select(a => a.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed record Subscription(IDisposable Handle, IExchangeAccountClient Client, string ApiKey);
}
