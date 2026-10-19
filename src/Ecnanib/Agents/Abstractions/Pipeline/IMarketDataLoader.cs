using Ecnanib.Database.Entities;
using Ecnanib.Exchange.Abstractions;
using Candle = Ecnanib.Exchange.Abstractions.Candle;

namespace Ecnanib.Agents.Abstractions;

/// <summary>The candles an evaluation sees: the agent's timeframe up to and including the closed candle, plus the higher timeframes the strategy needs (FR-STR-03) and the futures mark price (FR-DATA-04).</summary>
public sealed record MarketSnapshot(
    IReadOnlyList<Candle> Candles,
    IReadOnlyDictionary<CandleInterval, IReadOnlyList<Candle>> HigherTimeframes,
    MarkPrice? MarkPrice)
{
    public Candle? Latest => Candles.Count == 0 ? null : Candles[^1];
}

/// <summary>
/// The "load market data" step of FR-AGT-10. Seeded by the agents issue with the most recent closed candles from the
/// exchange; the market-data issue adds backfill on start, higher timeframes and the mark price (FR-DATA-02..04).
/// Register an implementation before <c>AddAgents</c> to replace the default.
/// </summary>
public interface IMarketDataLoader
{
    Task<MarketSnapshot> LoadAsync(Agent agent, MarketKey key, Candle closed, CancellationToken cancellationToken);
}
