using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Agents.Abstractions;

/// <summary>A candle series other than the agent's own symbol and timeframe (FR-STR-03, Appendix A filters), e.g. BTCUSDT weekly for the BTC regime check.</summary>
public readonly record struct ReferenceSeries(string Symbol, CandleInterval Interval);

/// <summary>
/// The candles an evaluation sees: the agent's timeframe up to and including the closed candle, the higher timeframes
/// and reference series the strategy declares (FR-STR-03), aligned so that nothing closed after the evaluated candle is
/// included, and the futures mark price when available (FR-DATA-04). An absent series reads as empty.
/// </summary>
public sealed record MarketSnapshot(
    IReadOnlyList<Candle> Candles,
    IReadOnlyDictionary<CandleInterval, IReadOnlyList<Candle>> HigherTimeframes,
    IReadOnlyDictionary<ReferenceSeries, IReadOnlyList<Candle>> References,
    MarkPrice? MarkPrice)
{
    public Candle? Latest => Candles.Count == 0 ? null : Candles[^1];

    public IReadOnlyList<Candle> Higher(CandleInterval interval) => HigherTimeframes.TryGetValue(interval, out var candles) ? candles : [];

    public IReadOnlyList<Candle> Reference(ReferenceSeries series) => References.TryGetValue(series, out var candles) ? candles : [];
}

/// <summary>
/// What the loader follows for a tracked agent: its own series and, when its strategy is known, the higher timeframes
/// and reference series the strategy declares (FR-STR-03).
/// </summary>
public sealed record MarketDataNeeds(Guid AgentId, MarketKey Key, CandleInterval Timeframe, StrategyDescriptor? Strategy);

/// <summary>
/// The "load market data" step of FR-AGT-10 and the series it keeps for the agents the scheduler tracks. The agents
/// module's default reads the most recent closed candles from the exchange on every evaluation; the market-data module
/// backfills on track, follows the streams, persists the candles for audit and keeps the futures mark price
/// (FR-DATA-02..05). Register an implementation before <c>AddAgents</c> to replace the default.
/// </summary>
public interface IMarketDataLoader
{
    /// <summary>
    /// Backfills and follows every series the agent will ask for (FR-DATA-02..04). Returns at once: the work goes on
    /// in the background and <see cref="LoadAsync"/> waits for it. Idempotent per agent; a later call replaces the needs.
    /// </summary>
    void Track(MarketDataNeeds needs);

    /// <summary>Releases what only this agent needed.</summary>
    void Untrack(Guid agentId);

    Task<MarketSnapshot> LoadAsync(Agent agent, StrategyDescriptor strategy, MarketKey key, Candle closed, CancellationToken cancellationToken);
}
