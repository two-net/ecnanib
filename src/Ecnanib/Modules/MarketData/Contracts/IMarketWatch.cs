using Ecnanib.Kernel;

namespace Ecnanib.Modules.MarketData.Contracts;

/// <summary>
/// Live prices and the candle forming now, for the pages that show an agent (FR-UI-05, FR-UI-03). One loop per market,
/// environment and pair reads Binance's public data every <c>Binance:LiveMarketIntervalSeconds</c> while anyone
/// watches it, shared by every watcher, and stops when the last one leaves. It reads only while the server's request
/// weight is low, so cycles and protective work always come first. Nothing is kept: a page that opens reads its closed
/// candles through <see cref="IMarketData.ReadCandlesAsync"/>. See Live market data in docs/architecture.md.
/// </summary>
public interface IMarketWatch
{
    /// <summary>
    /// Sends <paramref name="onUpdate"/> what's latest for <paramref name="key"/> after each read, until the returned
    /// subscription is disposed, which a page must do when it closes. The latest value, when one has been read, is
    /// sent at once, on the caller's thread. Otherwise the first comes within seconds. <paramref name="onUpdate"/> is
    /// called on the loop's thread, so it must return at once and never throw: one that throws is logged, and stops
    /// neither the loop nor the others. When too many pairs are watched at once, the subscription sends nothing.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol Binance wouldn't accept.</exception>
    IDisposable Watch(WatchKey key, Action<LiveMarket> onUpdate);
}

/// <param name="Symbol">Such as BTCUSDT.</param>
/// <param name="Timeframe">The candles whose forming one to send, or null for the price alone.</param>
public sealed record WatchKey(Market Market, TradingEnvironment Environment, string Symbol, Timeframe? Timeframe);

/// <summary>
/// One read of a watched pair. Prices are in the quote asset. Demo prices come from Binance's Demo servers, and
/// resemble Live markets without equalling them (EXT-BIN-08).
/// </summary>
/// <param name="Forming">The candle open now, which changes until it closes. Null without a timeframe.</param>
/// <param name="LastClosed">The candle before it, as Binance has it now, which a page adds when its forming one closes.</param>
/// <param name="LastPrice">The last trade's price: Spot's ticker, or on Futures the forming candle's close when there is one.</param>
/// <param name="MarkPrice">Futures: the mark price, which Binance values positions at.</param>
/// <param name="BidPrice">Spot: the best price a long could sell at now.</param>
/// <param name="AskPrice">Spot: the best price a short could buy back at now.</param>
/// <param name="At">When it was read, by this server's clock.</param>
/// <param name="Stale">
/// The latest read failed, or was skipped to spare request weight, so these are what was read at <paramref name="At"/>,
/// or nothing yet.
/// </param>
public sealed record LiveMarket(
    WatchKey Key,
    Candle? Forming,
    Candle? LastClosed,
    decimal? LastPrice,
    decimal? MarkPrice,
    decimal? BidPrice,
    decimal? AskPrice,
    DateTimeOffset At,
    bool Stale)
{
    /// <summary>The price to show for the pair: the mark price on Futures, and the last trade's on Spot.</summary>
    public decimal? Price => Key.Market == Market.Futures ? MarkPrice ?? LastPrice : LastPrice;

    /// <summary>
    /// What an open position would close at now, as a decision cycle values it: the mark price on Futures, and on Spot
    /// the bid for a long and the ask for a short.
    /// </summary>
    public decimal? ClosingPrice(bool isLong) => Key.Market == Market.Futures ? MarkPrice : isLong ? BidPrice : AskPrice;
}
