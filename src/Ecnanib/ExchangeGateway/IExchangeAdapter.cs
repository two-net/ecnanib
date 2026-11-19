using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// The common interface the exchange sits behind, so the rest of the application does not depend
/// on Binance's request formats. Implementations live under <c>ExchangeGateway/Adapters</c>.
/// Operations that place orders are in the <c>ExchangeGateway.Trading</c> namespace, which only
/// the Decision Engine may use: see <see cref="Trading.IExchangeOrders"/>. See docs/exchange.md.
/// </summary>
/// <remarks>
/// Every operation here reads. None withdraws, deposits, or transfers funds, and none will
/// (FR-BIN-9), and none adds margin to a position (FR-BIN-F5). A credential is taken with each call that needs one and is kept nowhere: the
/// gateway never fetches a stored key. Every call passes the one limiter of the gateway, which
/// keeps the service under its part of the exchange's limit on requests, and a call that needs a
/// credential says whose it is, so that users take turns there (FR-BIN-4). A call here is made
/// once, for whoever makes it. Market data, which is read once for everyone, is behind
/// <see cref="IExchangeMarketData"/>.
/// </remarks>
public interface IExchangeAdapter
{
    /// <summary>Reads the exchange's clock for one market and environment. It needs no credential.</summary>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The time, or how asking ended. An exchange that does not answer is an outcome: this does
    /// not fail for it.
    /// </returns>
    Task<ExchangeTime> GetServerTimeAsync(Market market, TradingMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the exchange about a credential, with calls that only read: whether it takes the
    /// credential, and what the credential may do (FR-KEY-3, FR-KEY-4).
    /// </summary>
    /// <param name="userId">
    /// Whose calls these are. Calls that wait under the limit on requests take turns, one user
    /// after the other (FR-BIN-4). It is not sent to the exchange.
    /// </param>
    /// <param name="mode">The environment the credential is for.</param>
    /// <param name="apiKey">
    /// The user's API key. It is passed on its own, as the secret key is, so that neither is part
    /// of a value that can be printed or logged.
    /// </param>
    /// <param name="secretKey">The secret key that belongs to it, which signs the calls and is never sent.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>
    /// What the exchange said. An exchange that does not answer is
    /// <see cref="KeyVerdict.Unreachable"/>: this does not fail for it.
    /// </returns>
    Task<ExchangeKeyCheck> CheckKeyAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);

    /// <summary>
    /// Reads what the account of a credential holds on one market, with a call that is signed
    /// with the credential and only reads.
    /// </summary>
    /// <param name="userId">Whose call it is, as in <see cref="CheckKeyAsync"/>.</param>
    /// <param name="market">The market whose account is read.</param>
    /// <param name="mode">The environment the credential is for, which is the one that is asked.</param>
    /// <param name="apiKey">The user's API key, passed on its own as in <see cref="CheckKeyAsync"/>.</param>
    /// <param name="secretKey">The secret key that belongs to it, which signs the call and is never sent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The balances, or how asking ended. An exchange that does not answer is an outcome: this
    /// does not fail for it. One that refuses the credential is
    /// <see cref="ExchangeCallOutcome.AuthenticationFailed"/>, which is when the credential is
    /// checked again.
    /// </returns>
    Task<ExchangeBalances> GetBalancesAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);

    /// <summary>
    /// Reads how the account of a credential stands on the Futures market for an agent that
    /// trades one symbol there with isolated margin, in one-way mode (FR-BIN-F2): the modes that
    /// hold for the whole account, which the service does not change, and whether the account
    /// holds a position in the symbol. The calls are signed with the credential, and only read.
    /// It takes no market: only the Futures market has positions.
    /// </summary>
    /// <param name="userId">Whose calls these are, as in <see cref="CheckKeyAsync"/>.</param>
    /// <param name="mode">The environment the credential is for, which is the one that is asked.</param>
    /// <param name="apiKey">The user's API key, passed on its own as in <see cref="CheckKeyAsync"/>.</param>
    /// <param name="secretKey">The secret key that belongs to it, which signs the calls and is never sent.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>
    /// How the account stands, or how asking ended. An exchange that does not answer is an
    /// outcome: this does not fail for it.
    /// </returns>
    Task<ExchangeFuturesAccount> GetFuturesAccountAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, string symbol, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the position that the account of a credential holds in one symbol of the Futures
    /// market, and how its margin stands: the price that it was entered at, the mark price, and
    /// the price that the exchange liquidates it at, which is where its margin runs out
    /// (FR-BIN-F5). It is one call, which is signed
    /// with the credential and only reads: nothing here adds margin to a position, or takes any
    /// out of one.
    /// </summary>
    /// <param name="userId">Whose call it is, as in <see cref="CheckKeyAsync"/>.</param>
    /// <param name="mode">The environment the credential is for, which is the one that is asked.</param>
    /// <param name="apiKey">The user's API key, passed on its own as in <see cref="CheckKeyAsync"/>.</param>
    /// <param name="secretKey">The secret key that belongs to it, which signs the call and is never sent.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The position, or how asking ended. An exchange that does not answer is an outcome: this
    /// does not fail for it. An account that holds a long and a short position in the symbol at
    /// once, which it does only in hedge mode, is not read as one position: that is
    /// <see cref="ExchangeCallOutcome.Failed"/>.
    /// </returns>
    Task<ExchangeFuturesPosition> GetFuturesPositionAsync(
        Guid userId, TradingMode mode, string apiKey, string secretKey, string symbol, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the funding that the account of a credential received and paid in one symbol of
    /// the Futures market between two moments, both of them included (FR-BIN-F4). It is one
    /// call, which is signed with the credential and only reads.
    /// </summary>
    /// <param name="userId">Whose call it is, as in <see cref="CheckKeyAsync"/>.</param>
    /// <param name="mode">The environment the credential is for, which is the one that is asked.</param>
    /// <param name="apiKey">The user's API key, passed on its own as in <see cref="CheckKeyAsync"/>.</param>
    /// <param name="secretKey">The secret key that belongs to it, which signs the call and is never sent.</param>
    /// <param name="symbol">The symbol.</param>
    /// <param name="from">The first moment that is asked for.</param>
    /// <param name="to">The last moment that is asked for.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// The funding, or how asking ended. An exchange that does not answer is an outcome: this
    /// does not fail for it. The exchange keeps what an account was paid for three months.
    /// </returns>
    /// <exception cref="ArgumentException">The last moment is before the first.</exception>
    Task<ExchangeFundings> GetFundingAsync(
        Guid userId,
        TradingMode mode,
        string apiKey,
        string secretKey,
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken);
}

/// <summary>
/// How the account of a credential stands on the Futures market, for one symbol. All of it is as
/// the exchange said when the read succeeded, and nothing of it says anything otherwise: an
/// account that could not be read is not one that nothing stands in the way of.
/// </summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="HedgeMode">
/// Whether the account keeps a long and a short position of a symbol apart, where one-way mode
/// has one position for a symbol. It holds for the whole account.
/// </param>
/// <param name="MultiAssetsMargin">
/// Whether the account margins its positions with more than one asset. The exchange takes no
/// isolated margin in that mode. It holds for the whole account.
/// </param>
/// <param name="HoldsPosition">Whether the account holds a position in the symbol, long or short.</param>
public sealed record ExchangeFuturesAccount(ExchangeCallOutcome Outcome, bool HedgeMode, bool MultiAssetsMargin, bool HoldsPosition)
{
    public bool HedgeMode { get; } = Outcome == ExchangeCallOutcome.Succeeded || !(HedgeMode || MultiAssetsMargin || HoldsPosition)
        ? HedgeMode
        : throw new ArgumentException("A reading says how an account stands only when it succeeded.", nameof(HedgeMode));

    /// <summary>A read that ended without saying how the account stands.</summary>
    public static ExchangeFuturesAccount Without(ExchangeCallOutcome outcome) => new(outcome, false, false, false);
}

/// <summary>
/// The position that the account of a credential holds in one symbol of the Futures market, and
/// how its margin stands, all of it as the exchange said in one answer (FR-BIN-F5). It says
/// something when the read succeeded, and a position when the account holds one.
/// </summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Quantity">
/// How much the position holds: above zero for a long position, below zero for a short one, and
/// zero when the account holds none in the symbol, or the read did not succeed.
/// </param>
/// <param name="EntryPrice">The average price that the position was entered at, or null where there is no position.</param>
/// <param name="MarkPrice">The mark price of the symbol, which a liquidation goes by, or null where there is no position.</param>
/// <param name="LiquidationPrice">
/// The mark price at which the exchange liquidates the position, which is where the margin of
/// the position no longer covers what it must keep, or null where the exchange names none: a
/// position without leverage may have none.
/// </param>
public sealed record ExchangeFuturesPosition(
    ExchangeCallOutcome Outcome, decimal Quantity, decimal? EntryPrice, decimal? MarkPrice, decimal? LiquidationPrice)
{
    public decimal Quantity { get; } = Quantity == 0m
        ? EntryPrice is null && MarkPrice is null && LiquidationPrice is null
            ? Quantity
            : throw new ArgumentException("A reading says the prices of a position, and there is none.", nameof(Quantity))
        : Outcome == ExchangeCallOutcome.Succeeded && EntryPrice is not null && MarkPrice is not null
            ? Quantity
            : throw new ArgumentException("A reading says a position only when it succeeded, with the prices of the position.", nameof(Quantity));

    /// <summary>Whether the account holds a position in the symbol.</summary>
    public bool Holds => Quantity != 0m;

    /// <summary>A read that ended without saying what the account holds, or one that says that it holds nothing in the symbol.</summary>
    public static ExchangeFuturesPosition Without(ExchangeCallOutcome outcome) => new(outcome, 0m, null, null, null);
}

/// <summary>The funding that an account received and paid in a symbol of the Futures market, in the time that was asked for.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Fundings">The funding, oldest first, when the read succeeded, and nothing otherwise. A list is whole or it is not a list.</param>
/// <param name="More">
/// Whether the exchange may have more of the time that was asked for than this: one answer holds
/// so many rows, and this one holds that many.
/// </param>
public sealed record ExchangeFundings(ExchangeCallOutcome Outcome, IReadOnlyList<ExchangeFunding> Fundings, bool More)
{
    public IReadOnlyList<ExchangeFunding> Fundings { get; } = Outcome == ExchangeCallOutcome.Succeeded || (Fundings.Count == 0 && !More)
        ? Fundings
        : throw new ArgumentException("A reading has funding only when it succeeded.", nameof(Fundings));

    /// <summary>A read that ended without saying any funding.</summary>
    public static ExchangeFundings Without(ExchangeCallOutcome outcome) => new(outcome, [], More: false);
}

/// <summary>What an account received or paid at one funding time, in one symbol of the Futures market.</summary>
/// <param name="TransactionId">The number that the exchange gave it, which is unique among the funding of an account.</param>
/// <param name="Amount">How much: above zero when the account received it, and below zero when the account paid it.</param>
/// <param name="Asset">The asset that it was received or paid in.</param>
/// <param name="At">When, by the clock of the exchange.</param>
public sealed record ExchangeFunding(string TransactionId, decimal Amount, string Asset, DateTimeOffset At);

/// <summary>What the exchange said about a credential.</summary>
/// <param name="Verdict">Whether it took the credential. The rest is known only when it did.</param>
/// <param name="SpotTrading">Whether the credential may trade on the Spot market.</param>
/// <param name="FuturesTrading">Whether the credential may trade on the Futures market.</param>
/// <param name="CanWithdraw">
/// Whether the credential may withdraw funds, or null where the exchange does not report it: its
/// test environment, which holds no real funds, does not.
/// </param>
/// <param name="IpRestricted">
/// Whether the credential works only from addresses that its owner listed, or null where the
/// exchange does not report it.
/// </param>
public sealed record ExchangeKeyCheck(
    KeyVerdict Verdict,
    bool SpotTrading = false,
    bool FuturesTrading = false,
    bool? CanWithdraw = null,
    bool? IpRestricted = null)
{
    /// <summary>The exchange refused the credential.</summary>
    public static ExchangeKeyCheck Refused { get; } = new(KeyVerdict.Refused);

    /// <summary>The exchange said nothing about the credential.</summary>
    public static ExchangeKeyCheck Unreachable { get; } = new(KeyVerdict.Unreachable);
}
