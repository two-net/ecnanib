using Ecnanib.Kernel;

namespace Ecnanib.Modules.Agents.Contracts;

/// <summary>
/// What an agent has on the exchange. Execution knows, and Agents may not depend on it, so Agents declares
/// this port and Execution implements it (see Inversions in docs/architecture.md). The kill switch and
/// stopping with a close (#37) will add to it.
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
}
