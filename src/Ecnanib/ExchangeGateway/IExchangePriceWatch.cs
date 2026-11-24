using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// The price of a symbol as it changes, for a page that shows it: prices update in the UI
/// within 2 seconds, without a manual refresh (FR-NTF-2). See "Following a price" in
/// docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>A price that is followed is read once a second, for everyone who follows it,</b> with the
/// reads of <see cref="IExchangeMarketData"/>, which are shared (FR-BIN-3). However many pages
/// show a symbol, the exchange is asked once. A price that nobody follows is not read.
/// </para>
/// <para>
/// <b>It is read only while the limit has room:</b> every read costs of the one budget that all
/// users spend (FR-BIN-4), and a page is nothing that an order waits for. See
/// <see cref="IExchangeLimits.HasRoom"/>. While it has none, and while the exchange does not
/// answer, what follows hears nothing, and keeps the price that it has, with its time.
/// </para>
/// <para>
/// It is no job, and nothing of it is stored: it reads for the pages of this instance, while
/// they are open (NFR-DAT-6).
/// </para>
/// </remarks>
public interface IExchangePriceWatch
{
    /// <summary>
    /// Follows a price, until what it returns is disposed of. What follows is told the price
    /// that the watch has, at once, when it has one, and then each price as it is read.
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment.</param>
    /// <param name="symbol">The symbol, which is one of the configuration file.</param>
    /// <param name="kind">Which price. The mark price is of the Futures market alone.</param>
    /// <param name="heard">
    /// What is told each price. It is called by the watch, so it returns at once, and what it
    /// throws is logged and reaches nobody.
    /// </param>
    /// <exception cref="ArgumentException">The mark price of a market that has none was asked for.</exception>
    IDisposable Follow(Market market, TradingMode mode, string symbol, ExchangePriceKind kind, Action<WatchedPrice> heard);

    /// <summary>
    /// The price as the watch last read it, or null when nobody follows it, or nothing was read
    /// yet. It asks the exchange nothing: a page that is written asks here, and so never waits
    /// for the exchange.
    /// </summary>
    WatchedPrice? Latest(Market market, TradingMode mode, string symbol, ExchangePriceKind kind);
}

/// <summary>Which price of a symbol.</summary>
public enum ExchangePriceKind
{
    /// <summary>The price that the symbol last traded at.</summary>
    LastTraded,

    /// <summary>The mark price of the Futures market, which the exchange values a position at and triggers its protective orders on.</summary>
    Mark,
}

/// <summary>A price as the watch read it.</summary>
/// <param name="Price">The price.</param>
/// <param name="ReadAt">When the read that gave it was sent, by the clock of the service.</param>
public sealed record WatchedPrice(decimal Price, DateTimeOffset ReadAt);
