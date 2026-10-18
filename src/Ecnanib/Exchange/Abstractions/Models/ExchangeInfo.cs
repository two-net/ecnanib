namespace Ecnanib.Exchange.Abstractions;

/// <summary>Exchange information for one market: tradable symbols with filters and the published rate limits (FR-DATA-01, EX-05).</summary>
public sealed record ExchangeInfo(
    DateTimeOffset ServerTime,
    IReadOnlyList<SymbolInfo> Symbols,
    IReadOnlyList<RateLimitRule> RateLimits);

/// <summary>A rate limit as published by the exchange in exchangeInfo.</summary>
public sealed record RateLimitRule(RateLimitKind Kind, TimeSpan Window, int Limit);
