using Ecnanib.Kernel;

namespace Ecnanib.Modules.ExchangeAdapter.Contracts;

/// <summary>
/// Signed reads of a user's Binance account. Credentials (#20) validates keys with it, and Execution
/// reconciles with it. Placing and cancelling orders is <see cref="IExchangeTrading"/>, which only
/// Execution may use. See Exchange adapter in docs/architecture.md.
/// </summary>
public interface IExchangeAccount
{
    /// <summary>The account behind <paramref name="key"/>, on the key's environment (FR-KEY-04).</summary>
    /// <exception cref="ExchangeException">The call failed. An invalid key is <see cref="ExchangeFailure.Rejected"/>.</exception>
    /// <exception cref="ArgumentException">An Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.</exception>
    Task<ExchangeAccount> GetAccountAsync(Market market, ExchangeKey key, CancellationToken cancellationToken);

    /// <summary>
    /// What a Live key is allowed to do (FR-KEY-05), from <c>GET /sapi/v1/account/apiRestrictions</c> on the
    /// Spot Live server. Binance documents this endpoint for Live only, so a Demo key is refused.
    /// </summary>
    /// <exception cref="ExchangeException">
    /// The call failed. An invalid key, or an address the key's IP restriction leaves out, is
    /// <see cref="ExchangeFailure.Rejected"/>: Binance gives both -2015.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A Demo key, or an Ed25519 secret that isn't a PKCS#8 PEM private key. Nothing was sent.
    /// </exception>
    Task<ApiRestrictions> GetApiRestrictionsAsync(ExchangeKey key, CancellationToken cancellationToken);

    /// <summary>
    /// An order by its client order ID, or null when Binance says it doesn't exist and it can't appear
    /// later. This is the query that settles an unknown outcome before anything is sent again
    /// (FR-EXE-05): only null allows that. Use <see cref="RequestPriority.Protective"/> when it's about a
    /// protective order.
    /// </summary>
    /// <exception cref="ExchangeException">
    /// The call failed, which says nothing about whether the order exists. It's
    /// <see cref="ExchangeFailure.Unknown"/> with <see cref="ExchangeException.SettlesAt"/> when Binance has
    /// no such order yet, but a send of it with an unknown outcome could still be accepted.
    /// </exception>
    Task<ExchangeOrder?> GetOrderAsync(
        Market market,
        ExchangeKey key,
        OrderReference order,
        RequestPriority priority,
        CancellationToken cancellationToken);
}

/// <param name="CanTrade">Spot only. Binance's Futures account doesn't say.</param>
/// <param name="Balances">Assets with a balance. Spot leaves out zero balances.</param>
/// <param name="TotalMarginBalance">Futures only.</param>
/// <param name="AvailableBalance">Futures only.</param>
/// <param name="Uid">Spot only: Binance's ID for the account, which tells whether two keys belong to one account.</param>
public sealed record ExchangeAccount(
    Market Market,
    bool? CanTrade,
    IReadOnlyList<AssetBalance> Balances,
    decimal? TotalMarginBalance,
    decimal? AvailableBalance,
    long? Uid = null);

/// <summary>A Live key's permissions, as Binance reports them (FR-KEY-05).</summary>
/// <param name="IpRestricted">Whether the key only works from the addresses listed for it.</param>
/// <param name="Reading">Whether it can read the account.</param>
/// <param name="Withdrawals">Whether it can withdraw funds, which Ecnanib never accepts (AC-04).</param>
/// <param name="SpotAndMarginTrading">Whether it can trade Spot.</param>
/// <param name="Futures">
/// Whether it can trade Futures. Binance says a key made before the Futures account was opened can't
/// use the Futures API, so check with a Futures request too.
/// </param>
public sealed record ApiRestrictions(bool IpRestricted, bool Reading, bool Withdrawals, bool SpotAndMarginTrading, bool Futures);

/// <param name="Total">Spot: free plus locked. Futures: the wallet balance.</param>
/// <param name="Available">Spot: free. Futures: available.</param>
public sealed record AssetBalance(string Asset, decimal Total, decimal Available);
