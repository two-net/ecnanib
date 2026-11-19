using Ecnanib.Shared;

namespace Ecnanib.ExchangeGateway;

/// <summary>
/// The account event feed of the exchange (FR-BIN-7): it says when something changes in the
/// account of a user on a market, as it happens, where the exchange has such a feed and the
/// configuration file names its address. See "The account event feed" in docs/exchange.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>An event is a signal, and no record.</b> A change says that something of a symbol changed
/// in an account, and not what: no status, no quantity, and no price of an event leaves the
/// gateway. Whoever keeps the orders and the positions asks the exchange what changed, with the
/// calls that read, and writes what those say. So an event that is missed, late, or not what the
/// exchange's documentation shows changes nothing but when the question is asked.
/// </para>
/// <para>
/// <b>It only reads.</b> Following an account places no order and moves nothing (FR-BIN-9).
/// </para>
/// <para>
/// <b>The credential comes with the call, and is kept nowhere</b> (FR-KEY-9), as with every
/// call of the gateway. It is needed when an account begins to be followed, and again when the
/// exchange wants the feed renewed: <see cref="StateOf"/> says when. Whoever follows an account
/// says so again and again, with <see cref="Keep"/>: a feed that nobody has asked for in a while
/// is closed, so that an instance which no longer does the background work follows nothing.
/// </para>
/// <para>
/// A feed that ends, because the exchange closed it or the connection broke, is no error: the
/// account is no longer followed, and that is said as a change of everything in it, since what
/// happened meanwhile was not heard.
/// </para>
/// </remarks>
public interface IExchangeAccountFeed
{
    /// <summary>Whether the exchange has an account event feed on a market in an environment: the configuration file names its address.</summary>
    bool IsAvailable(Market market, TradingMode mode);

    /// <summary>How the feed of the account of a user on a market in an environment stands.</summary>
    ExchangeFeedState StateOf(Guid userId, Market market, TradingMode mode);

    /// <summary>
    /// Follows the account of a credential on a market in an environment: opens its feed, or
    /// renews one that is open where the exchange wants that. It is asked when
    /// <see cref="StateOf"/> says that the account is not followed, or that a renewal is due.
    /// </summary>
    /// <param name="userId">Whose account it is, which every change of the feed then says. It is not sent to the exchange.</param>
    /// <param name="market">The market.</param>
    /// <param name="mode">The environment that the credential is for.</param>
    /// <param name="apiKey">The user's API key.</param>
    /// <param name="secretKey">The secret key that belongs to it, which signs and is never sent.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How it ended. Only <see cref="ExchangeFeedOutcome.Following"/> says that the account is followed.</returns>
    /// <exception cref="ArgumentException">No user is named.</exception>
    Task<ExchangeFeedOutcome> FollowAsync(
        Guid userId, Market market, TradingMode mode, string apiKey, string secretKey, CancellationToken cancellationToken);

    /// <summary>
    /// Says that the feed of an account is still wanted. A feed that nobody kept for
    /// <see cref="ExchangeFeedTimes.IdleAfter"/> is closed. It says nothing of an account that is not followed.
    /// </summary>
    void Keep(Guid userId, Market market, TradingMode mode);

    /// <summary>
    /// Takes the changes that the feeds have said since they were last taken, each once however
    /// often it was said, and waits for one when there is none.
    /// </summary>
    /// <param name="wait">How long it waits for a change when there is none. Nothing waits for no time.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The changes, of every account that is followed. None when none came in that time.</returns>
    Task<IReadOnlyList<ExchangeAccountChange>> TakeAsync(TimeSpan wait, CancellationToken cancellationToken);
}

/// <summary>The times of an account event feed, which are fixed, as the time limit of a call is.</summary>
public static class ExchangeFeedTimes
{
    /// <summary>For how long a feed that nobody says is still wanted stays open.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(3);
}

/// <summary>How the feed of an account stands.</summary>
public enum ExchangeFeedState
{
    /// <summary>The account is not followed: it never was, its feed ended, or nobody kept it.</summary>
    NotFollowed,

    /// <summary>The account is followed.</summary>
    Followed,

    /// <summary>The account is followed, and the exchange wants its feed renewed soon, which takes the credential.</summary>
    RenewalDue,
}

/// <summary>How following an account ended.</summary>
public enum ExchangeFeedOutcome
{
    /// <summary>The account is followed.</summary>
    Following,

    /// <summary>The market has no feed in the environment: the configuration file names no address for it.</summary>
    NotAvailable,

    /// <summary>The exchange answered as it answers a wrong credential. The credential is checked again.</summary>
    KeyRefused,

    /// <summary>
    /// Nothing was opened now: the limit on requests has no room for it, the exchange signalled
    /// that it has had too many, or enough feeds were opened in the last minute. It is asked again later.
    /// </summary>
    NotNow,

    /// <summary>Anything else: no connection, a fault of the exchange, or an answer that is not what the exchange sends.</summary>
    Failed,
}

/// <summary>
/// Something changed in the account of a user on a market in an environment, as its feed said:
/// an order, or what the account holds.
/// </summary>
/// <param name="UserId">Whose account.</param>
/// <param name="Market">The market.</param>
/// <param name="Mode">The environment.</param>
/// <param name="Symbol">
/// The symbol that it is about, or null when it is about everything of the account: the feed
/// began or ended, so what happened before or meanwhile was not heard, or an event did not say
/// its symbol.
/// </param>
public sealed record ExchangeAccountChange(Guid UserId, Market Market, TradingMode Mode, string? Symbol);
