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
/// The "load market data" step of FR-AGT-10. Seeded by the agents issue with the most recent closed candles from the
/// exchange for every series the strategy declares; the market-data issue adds backfill on start, caching and streamed
/// mark prices (FR-DATA-02..04). Register an implementation before <c>AddAgents</c> to replace the default.
/// </summary>
public interface IMarketDataLoader
{
    Task<MarketSnapshot> LoadAsync(Agent agent, StrategyDescriptor strategy, MarketKey key, Candle closed, CancellationToken cancellationToken);
}
