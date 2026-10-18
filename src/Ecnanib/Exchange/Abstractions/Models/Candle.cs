namespace Ecnanib.Exchange.Abstractions;

/// <summary>One kline. <see cref="IsClosed"/> is false for the still-forming candle of a stream update.</summary>
public sealed record Candle(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal QuoteVolume,
    long TradeCount,
    bool IsClosed);

public sealed record PriceTick(string Symbol, decimal Price, DateTimeOffset Timestamp);

/// <summary>USD-M futures mark price with funding information (FR-DATA-04).</summary>
public sealed record MarkPrice(
    string Symbol,
    decimal Mark,
    decimal? Index,
    decimal FundingRate,
    DateTimeOffset NextFundingTime,
    DateTimeOffset Timestamp);
