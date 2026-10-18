namespace Ecnanib.Exchange.Abstractions;

/// <summary>
/// Public market data for one environment and market, retrieved without any user key (EX-06).
/// Obtain instances from <see cref="IExchangeClientFactory.GetPublicData"/>.
/// </summary>
public interface IPublicMarketData
{
    ExchangeEnvironment Environment { get; }

    MarketType Market { get; }

    Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct);

    /// <summary>Tradable symbols with their filters, plus the exchange's published rate limits (FR-DATA-01).</summary>
    Task<ExchangeInfo> GetExchangeInfoAsync(CancellationToken ct);

    /// <summary>One klines request. The exchange caps <paramref name="limit"/> (1000 spot, 1500 futures); result is ascending.</summary>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol, CandleInterval interval, DateTimeOffset? from, DateTimeOffset? to, int? limit, CancellationToken ct);

    /// <summary>
    /// The most recent <paramref name="count"/> candles, paging backwards as needed (FR-DATA-02 backfill). When
    /// <paramref name="closedOnly"/> is true the still-forming candle is excluded. Result is ascending by open time.
    /// </summary>
    Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(
        string symbol, CandleInterval interval, int count, bool closedOnly, CancellationToken ct);

    Task<PriceTick> GetPriceAsync(string symbol, CancellationToken ct);

    /// <summary>USD-M futures only (FR-DATA-04). Throws <see cref="NotSupportedException"/> on spot.</summary>
    Task<MarkPrice> GetMarkPriceAsync(string symbol, CancellationToken ct);
}
