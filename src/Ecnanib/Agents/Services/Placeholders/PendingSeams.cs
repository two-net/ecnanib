using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Agents.Services.Placeholders;

// The pipeline stages other issues deliver, seeded here so the runtime is complete and fail-safe (NFR-REL-03): no
// setup is detected, no decision is made and nothing is executed until the real implementation replaces the default.

/// <summary>Reports no bias and no setup until the strategy issue (FR-STR) delivers the Appendix A engines.</summary>
internal sealed class PendingStrategyEngine : IStrategyEngine
{
    public const string Note = "The strategy engine arrives with FR-STR (#8); no setup is detected yet.";

    public StrategyOutput Evaluate(StrategyContext context) => StrategyOutput.Nothing(Note);
}

/// <summary>Answers with no decision until the council issue (FR-AI) delivers the roles; the pipeline treats that as a Risk Manager failure (FR-AI-12).</summary>
internal sealed class PendingCouncil : ICouncil
{
    public const string Failure = "The council arrives with FR-AI (#9).";

    public Task<CouncilResult> DeliberateAsync(CouncilContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CouncilResult.Failed(Failure));
}

/// <summary>Records the decision and places nothing until the execution issue (FR-EXE) delivers sizing and limits.</summary>
internal sealed class PendingExecutionService : IExecutionService
{
    public const string Note = "Execution arrives with FR-EXE (#13); the decision was recorded and nothing was placed.";

    public Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ExecutionResult([], Note));
}

/// <summary>
/// The live venue until the execution issue delivers it: it can confirm that a live agent has nothing open, which is
/// all a Stop needs today, and refuses anything else.
/// </summary>
internal sealed class PendingLiveOrderVenue : IOrderVenue
{
    public const string NotAvailable = "Live execution arrives with FR-EXE (#13).";

    private readonly IDbContextFactory<EcnanibDbContext> _factory;

    public PendingLiveOrderVenue(IDbContextFactory<EcnanibDbContext> factory)
    {
        _factory = factory;
    }

    public AgentMode Mode => AgentMode.Live;

    public Task<Order> PlaceEntryAsync(Agent agent, EntryPlacement placement, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotAvailable);

    public Task<Order> PlaceCloseAsync(Agent agent, Trade trade, Guid? decisionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotAvailable);

    public async Task<int> CancelOpenOrdersAsync(Agent agent, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var open = await db.Orders.CountAsync(o => o.AgentId == agent.Id && !o.IsPaper && (o.Status == OrderStatus.New || o.Status == OrderStatus.PartiallyFilled), cancellationToken).ConfigureAwait(false);
        return open == 0 ? 0 : throw new NotSupportedException(NotAvailable);
    }

    public async Task<int> ClosePositionsAtMarketAsync(Agent agent, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var open = await db.Trades.CountAsync(t => t.AgentId == agent.Id && !t.IsPaper && t.ClosedAt == null, cancellationToken).ConfigureAwait(false);
        return open == 0 ? 0 : throw new NotSupportedException(NotAvailable);
    }

    public Task<decimal> GetEquityAsync(Agent agent, CancellationToken cancellationToken) =>
        throw new NotSupportedException(NotAvailable);
}

/// <summary>Picks the venue by mode; the first registration wins, so a live venue registered before <c>AddAgents</c> replaces the pending one.</summary>
internal sealed class OrderVenues : IOrderVenues
{
    private readonly IReadOnlyList<IOrderVenue> _venues;

    public OrderVenues(IEnumerable<IOrderVenue> venues)
    {
        _venues = venues.ToList();
    }

    public IOrderVenue For(AgentMode mode) =>
        _venues.FirstOrDefault(v => v.Mode == mode) ?? throw new InvalidOperationException($"No order venue is registered for {mode} mode.");
}

/// <summary>
/// The "load market data" step until the market-data issue (FR-DATA) delivers backfill, higher timeframes and the mark
/// price: the most recent closed candles of the agent's timeframe from the exchange, up to the candle being evaluated,
/// written through to <c>candles</c> for audit (FR-DATA-05).
/// </summary>
internal sealed class RecentCandleLoader : IMarketDataLoader
{
    public const int CandleCount = 300;

    private readonly IExchangeClientFactory _exchange;
    private readonly CandleStore _store;

    public RecentCandleLoader(IExchangeClientFactory exchange, CandleStore store)
    {
        _exchange = exchange;
        _store = store;
    }

    public async Task<MarketSnapshot> LoadAsync(Agent agent, MarketKey key, Candle closed, CancellationToken cancellationToken)
    {
        var data = _exchange.GetPublicData(key.Environment, key.Market);
        var recent = await data.GetRecentCandlesAsync(key.Symbol, agent.Timeframe, CandleCount, closedOnly: true, cancellationToken).ConfigureAwait(false);
        var candles = recent.Where(c => c.OpenTime <= closed.OpenTime).ToList();
        if (candles.Count == 0 || candles[^1].OpenTime < closed.OpenTime)
            candles.Add(closed);
        await _store.StoreManyAsync(key, agent.Timeframe, candles, cancellationToken).ConfigureAwait(false);
        return new MarketSnapshot(candles, new Dictionary<CandleInterval, IReadOnlyList<Candle>>(), null);
    }
}
