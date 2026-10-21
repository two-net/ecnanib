using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.MarketData.Abstractions;

/// <summary>
/// The futures metrics the module tracks for every USDⓈ-M symbol an agent follows (FR-DATA-04): the latest mark
/// price, index price, funding rate and next funding time from the mark-price stream.
/// </summary>
public interface IFuturesMetrics
{
    /// <summary>The latest streamed value, or null for a spot key, a symbol no agent follows or before the first update arrived.</summary>
    MarkPrice? Latest(MarketKey key);
}
