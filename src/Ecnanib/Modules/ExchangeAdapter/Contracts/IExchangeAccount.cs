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
    Task<ExchangeAccount> GetAccountAsync(Market market, ExchangeKey key, CancellationToken cancellationToken);

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
public sealed record ExchangeAccount(
    Market Market,
    bool? CanTrade,
    IReadOnlyList<AssetBalance> Balances,
    decimal? TotalMarginBalance,
    decimal? AvailableBalance);

/// <param name="Total">Spot: free plus locked. Futures: the wallet balance.</param>
/// <param name="Available">Spot: free. Futures: available.</param>
public sealed record AssetBalance(string Asset, decimal Total, decimal Available);
