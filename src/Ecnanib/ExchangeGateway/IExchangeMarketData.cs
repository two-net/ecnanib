using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// The market data of the exchange: candles, prices, and the rules of the symbols, and on the
/// Futures market also the mark price, the funding rate, and the open interest. It is read once
/// and shared by everyone who needs it (FR-BIN-3): the exchange counts its limit on requests
/// against the service as a whole, so a read for each agent would spend the budget of all users.
/// See docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// Whoever asks first causes the read. Whoever asks while that read is under way waits for the
/// same read, and whoever asks while what was read still counts gets it without a call. How a
/// read ended is shared in the same way, a failure included, which then counts for a few seconds.
/// </para>
/// <para>
/// What is read is kept in memory, for a short time, and nowhere else: candles for as long as
/// the cycles that begin with a candle take to start, and never in the database (NFR-DAT-6).
/// There is no range of the past to ask for.
/// </para>
/// <para>
/// A read is the service's own call. It needs no credential, and takes the turn of the service
/// at the limiter. Each market has data of its own in each environment: an agent decides on the
/// data of the environment that its orders go to.
/// </para>
/// <para>
/// Only the symbols of the configuration file are served. An operation ends with an error when
/// its caller cancelled, or when what it was given is no call: a symbol that the file does not
/// list, or a market, an environment, or an interval that does not exist. Everything else is an
/// outcome.
/// </para>
/// </remarks>
public interface IExchangeMarketData
{
    /// <summary>
    /// The newest closed candles of a symbol, oldest first: up to <see cref="ExchangeCandles.Most"/>
    /// of them, of which an asker takes the newest that it needs. The candle that is in progress
    /// is never among them (FR-CYC-1).
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment.</param>
    /// <param name="symbol">The symbol, which is one of the configuration file.</param>
    /// <param name="interval">The length of a candle.</param>
    /// <param name="cancellationToken">Ends the wait of this asker. The read goes on for the others.</param>
    /// <returns>
    /// The candles, or how reading them ended. The newest says through when they reach: an
    /// asker that needs the candle of a close looks at it, because the exchange may not have
    /// begun the next candle yet.
    /// </returns>
    Task<ExchangeCandles> GetCandlesAsync(
        Market market, TradingMode mode, string symbol, CandleInterval interval, CancellationToken cancellationToken);

    /// <summary>The price that a symbol last traded at.</summary>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment.</param>
    /// <param name="symbol">The symbol, which is one of the configuration file.</param>
    /// <param name="cancellationToken">Ends the wait of this asker.</param>
    Task<ExchangePrice> GetPriceAsync(Market market, TradingMode mode, string symbol, CancellationToken cancellationToken);

    /// <summary>
    /// The symbols of the configuration file that a market has, each with the rules that an
    /// order in it must keep (FR-AGT-6, FR-RSK-4).
    /// </summary>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment.</param>
    /// <param name="cancellationToken">Ends the wait of this asker.</param>
    Task<ExchangeSymbols> GetSymbolsAsync(Market market, TradingMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// The mark price of a symbol on the Futures market, with its funding rate and the time of
    /// the next funding (FR-BIN-F4).
    /// </summary>
    /// <param name="mode">The environment.</param>
    /// <param name="symbol">The symbol, which is one of the configuration file.</param>
    /// <param name="cancellationToken">Ends the wait of this asker.</param>
    Task<ExchangeMarkPrice> GetMarkPriceAsync(TradingMode mode, string symbol, CancellationToken cancellationToken);

    /// <summary>The open interest of a symbol on the Futures market.</summary>
    /// <param name="mode">The environment.</param>
    /// <param name="symbol">The symbol, which is one of the configuration file.</param>
    /// <param name="cancellationToken">Ends the wait of this asker.</param>
    Task<ExchangeOpenInterest> GetOpenInterestAsync(TradingMode mode, string symbol, CancellationToken cancellationToken);
}
