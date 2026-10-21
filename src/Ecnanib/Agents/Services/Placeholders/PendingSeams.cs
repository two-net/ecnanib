using Ecnanib.Agents.Abstractions;
using Ecnanib.Database;
using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Agents.Services.Placeholders;

// The pipeline stages other issues deliver, seeded here so the runtime is complete and fail-safe (NFR-REL-03): no
// decision is made and nothing is executed until the real implementation replaces the default.

/// <summary>Answers with no decision until the council issue (FR-AI) delivers the roles; the pipeline treats that as a Risk Manager failure (FR-AI-12).</summary>
internal sealed class PendingCouncil : ICouncil
{
    public const string Failure = "The council arrives with FR-AI (#9).";

    public Task<CouncilResult> DeliberateAsync(CouncilContext context, CancellationToken cancellationToken) =>
        Task.FromResult(CouncilResult.Failed(Failure));
}

/// <summary>Records no experience until the experience module (FR-EXP) is registered before <c>AddAgents</c>; the pipeline runs without shadow outcomes.</summary>
internal sealed class NoExperienceTracker : IExperienceTracker
{
    public Task TrackAsync(ExperienceContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Suggests nothing until the experience module (FR-EXP-08) is registered before <c>AddAgents</c>; every role gets its Appendix B default.</summary>
internal sealed class NoCouncilSuggestions : ICouncilSuggestions
{
    public Task<IReadOnlyList<CouncilSuggestion>> SuggestAsync(Guid userId, CandleInterval timeframe, MarketType marketType, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CouncilSuggestion>>([]);
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

/// <summary>Reads the exchange's listing on every call until the market-data module (FR-DATA-01, #12) keeps one per environment and market and refreshes it.</summary>
internal sealed class UncachedSymbolCatalog : ISymbolCatalog
{
    private readonly IExchangeClientFactory _exchange;

    public UncachedSymbolCatalog(IExchangeClientFactory exchange)
    {
        _exchange = exchange;
    }

    public async Task<SymbolInfo?> FindAsync(ExchangeEnvironment environment, MarketType market, string symbol, CancellationToken cancellationToken)
    {
        var symbols = await ListAsync(environment, market, cancellationToken).ConfigureAwait(false);
        return symbols.FirstOrDefault(s => string.Equals(s.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<SymbolInfo>> ListAsync(ExchangeEnvironment environment, MarketType market, CancellationToken cancellationToken)
    {
        var info = await _exchange.GetPublicData(environment, market).GetExchangeInfoAsync(cancellationToken).ConfigureAwait(false);
        return info.Symbols;
    }
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
/// The "load market data" step until the market-data module (FR-DATA, #12) is registered before <c>AddAgents</c>:
/// the most recent closed candles of the agent's timeframe from the exchange up to the candle being evaluated, the
/// higher timeframes and reference series the strategy declares (FR-STR-03) aligned so that no candle closing after
/// the evaluated one is included, and the futures mark price (FR-DATA-04). Reference series always come from
/// production public data, so a testnet agent judges the regime on real-market prices. The own and higher-timeframe
/// series must load (no bias data, no evaluation); a reference or mark-price failure is logged and the series left
/// absent. Nothing is backfilled, kept or persisted: up to four REST reads per evaluation.
/// </summary>
internal sealed class RecentCandleLoader : IMarketDataLoader
{
    public const int CandleCount = 300;

    private readonly IExchangeClientFactory _exchange;
    private readonly ILogger<RecentCandleLoader> _logger;

    public RecentCandleLoader(IExchangeClientFactory exchange, ILogger<RecentCandleLoader> logger)
    {
        _exchange = exchange;
        _logger = logger;
    }

    public void Track(MarketDataNeeds needs)
    {
    }

    public void Untrack(Guid agentId)
    {
    }

    public async Task<MarketSnapshot> LoadAsync(Agent agent, StrategyDescriptor strategy, MarketKey key, Candle closed, CancellationToken cancellationToken)
    {
        var data = _exchange.GetPublicData(key.Environment, key.Market);
        var cutoff = closed.OpenTime + agent.Timeframe.ToTimeSpan();

        var recent = await data.GetRecentCandlesAsync(key.Symbol, agent.Timeframe, CandleCount, closedOnly: true, cancellationToken).ConfigureAwait(false);
        var candles = recent.Where(c => c.OpenTime <= closed.OpenTime).ToList();
        if (candles.Count == 0 || candles[^1].OpenTime < closed.OpenTime)
            candles.Add(closed);

        var higher = new Dictionary<CandleInterval, IReadOnlyList<Candle>>();
        foreach (var interval in strategy.HigherTimeframes.Where(i => i != agent.Timeframe).Distinct())
        {
            var series = await LoadSeriesAsync(data, key, interval, cutoff, cancellationToken).ConfigureAwait(false);
            higher[interval] = series;
        }

        var references = new Dictionary<ReferenceSeries, IReadOnlyList<Candle>>();
        foreach (var reference in strategy.References.Distinct())
        {
            if (key.Environment == ExchangeEnvironment.Production && reference.Symbol == key.Symbol && higher.TryGetValue(reference.Interval, out var own))
            {
                references[reference] = own;
                continue;
            }

            var referenceKey = new MarketKey(ExchangeEnvironment.Production, key.Market, reference.Symbol);
            try
            {
                var production = key.Environment == ExchangeEnvironment.Production ? data : _exchange.GetPublicData(ExchangeEnvironment.Production, key.Market);
                references[reference] = await LoadSeriesAsync(production, referenceKey, reference.Interval, cutoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reference series {Symbol} {Interval} for agent {AgentId} could not be loaded; evaluating without it", reference.Symbol, reference.Interval.ToLabel(), agent.Id);
            }
        }

        MarkPrice? mark = null;
        if (key.Market == MarketType.UsdFutures)
        {
            try
            {
                mark = await data.GetMarkPriceAsync(key.Symbol, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mark price of {Symbol} for agent {AgentId} could not be loaded; evaluating without it", key.Symbol, agent.Id);
            }
        }

        return new MarketSnapshot(candles, higher, references, mark);
    }

    /// <summary>The recent closed candles of one series that closed at or before <paramref name="cutoff"/> (no look-ahead).</summary>
    private static async Task<IReadOnlyList<Candle>> LoadSeriesAsync(IPublicMarketData data, MarketKey key, CandleInterval interval, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var recent = await data.GetRecentCandlesAsync(key.Symbol, interval, CandleCount, closedOnly: true, cancellationToken).ConfigureAwait(false);
        var span = interval.ToTimeSpan();
        return recent.Where(c => c.OpenTime + span <= cutoff).ToList();
    }
}
