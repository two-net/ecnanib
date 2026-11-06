using Ecnanib.Kernel;

namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// What an agent has on the exchange, and what its Futures account allows. Execution knows, and Agents may not
/// depend on it, so Agents declares this port and Execution implements it (see Inversions in
/// docs/architecture.md). The kill switch and stopping with a close (#37) will add to it.
/// </summary>
public interface IAgentExposure
{
    /// <summary>
    /// Whether the agent has an open position or an open order, which locks its market, environment, pair,
    /// timeframe and exchange key (FR-AGT-03) and stops it being archived.
    /// </summary>
    Task<bool> HasOpenPositionsOrOrdersAsync(Guid userId, Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// How much of <paramref name="asset"/> the account behind the user's exchange key has available on
    /// <paramref name="market"/>, for FR-AGT-13's warning. Null when it isn't known, such as when Binance
    /// can't be reached or the key can't be used. It may be up to a minute old.
    /// </summary>
    Task<decimal?> GetAvailableBalanceAsync(
        Guid userId, Guid credentialId, Market market, string asset, CancellationToken cancellationToken);

    /// <summary>
    /// What the Futures account behind the user's exchange key says about EXT-BIN-07's prerequisites: whether it
    /// may trade, its position mode, its Multi-Assets mode, and the margin it has available in
    /// <paramref name="marginAsset"/>, the pair's quote asset. Or why it couldn't be read. It may be up to a minute
    /// old, because a page's clicks never reach the rate limiter.
    /// </summary>
    Task<FuturesAccountRead> ReadFuturesAccountAsync(
        Guid userId, Guid credentialId, string marginAsset, CancellationToken cancellationToken);
}

/// <summary>A Futures account as EXT-BIN-07 checks it.</summary>
/// <param name="CanTrade">Whether Binance lets the account trade Futures.</param>
/// <param name="HedgeMode">Whether the account is in Hedge Mode. Agents need One-way Mode (FR-RISK-09).</param>
/// <param name="MultiAssetsMode">Whether the account is in Multi-Assets Mode, which allows cross margin only, so isolated can't be set.</param>
/// <param name="AvailableMargin">What the account has available in the margin asset asked about.</param>
public sealed record FuturesAccountState(bool CanTrade, bool HedgeMode, bool MultiAssetsMode, decimal AvailableMargin);

/// <summary>Why a Futures account couldn't be read.</summary>
public enum FuturesAccountFailure
{
    /// <summary>The key can't be used on Futures: Binance refused it there, or its credential changed.</summary>
    KeyRefused,

    /// <summary>Binance couldn't be reached, or is limiting requests. Trying again later may work.</summary>
    Unavailable,
}

/// <summary>A Futures account's <see cref="State"/>, or the <see cref="Failure"/> that kept it from being read.</summary>
public sealed record FuturesAccountRead(FuturesAccountState? State, FuturesAccountFailure? Failure)
{
    public static FuturesAccountRead Of(FuturesAccountState state) => new(state, null);

    public static FuturesAccountRead Failed(FuturesAccountFailure failure) => new(null, failure);
}
