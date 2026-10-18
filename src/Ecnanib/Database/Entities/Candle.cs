using Ecnanib.Exchange.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// A closed candle retained for audit (FR-DATA-05). Public market data shared by all agents, keyed like
/// <c>MarketKey</c> plus interval and open time; swept after the configured retention (NFR-DATA-02/03).
/// Name collides with <c>Ecnanib.Exchange.Abstractions.Candle</c>; alias one of them where both are used.
/// </summary>
public sealed class Candle
{
    public ExchangeEnvironment Environment { get; set; }

    public MarketType MarketType { get; set; }

    public required string Symbol { get; set; }

    public CandleInterval Interval { get; set; }

    public DateTimeOffset OpenTime { get; set; }

    public DateTimeOffset CloseTime { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public decimal Volume { get; set; }

    public decimal QuoteVolume { get; set; }

    public long TradeCount { get; set; }
}
