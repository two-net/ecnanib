using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>
/// An agent's account, as a decision cycle needs it (FR-CYC-02 step 2): the balances on Binance, the symbol's leverage
/// brackets on Futures, and the agent's own pending entry, from Execution's records. Its own position waits for
/// reconciliation (#35), and until then execution refuses an entry while one is held. See Decision cycle in
/// docs/architecture.md.
/// </summary>
public interface IAgentAccounts
{
    /// <summary>Reads the account through the agent's exchange key, now: a cycle never works from an old balance.</summary>
    /// <param name="symbol">The pair, such as BTCUSDT, whose leverage brackets Futures reports.</param>
    /// <param name="baseAsset">The pair's base asset, whose holding Spot reports.</param>
    /// <param name="quoteAsset">The pair's quote asset, whose free balance Spot reports.</param>
    /// <exception cref="AgentAccountException">The key can't be used, or Binance couldn't give the balances.</exception>
    Task<AgentAccount> ReadAsync(
        Guid userId,
        Guid agentId,
        Guid credentialId,
        Market market,
        string symbol,
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken);
}

/// <param name="AvailableQuote">Spot: the quote asset's free balance. Futures: the available margin balance.</param>
/// <param name="BaseHeld">Spot only: all of the base asset, free or locked. Null on Futures.</param>
/// <param name="Position">
/// The agent's own open position, or null. On Spot, only what its own fills bought (FR-AGT-09). Always null until
/// reconciliation (#35).
/// </param>
/// <param name="HasPendingEntry">The agent has an entry order that hasn't filled.</param>
/// <param name="Brackets">
/// Futures only: the symbol's leverage brackets on the account, lowest first, which the liquidation buffer needs.
/// Empty on Spot, and when Binance couldn't give them, which fails every Futures entry but no exit.
/// </param>
public sealed record AgentAccount(
    decimal AvailableQuote,
    decimal? BaseHeld,
    OpenPosition? Position,
    bool HasPendingEntry,
    IReadOnlyList<LeverageBracket> Brackets);

/// <summary>Why an agent's account couldn't be read. Stored by name, so a value is never renamed.</summary>
public enum AgentAccountFailure
{
    /// <summary>The exchange key was deleted or stopped being valid, which its credential's checks deal with.</summary>
    KeyUnusable,

    /// <summary>Binance couldn't be reached, refused the request, or is limiting requests.</summary>
    ExchangeUnavailable,
}

public sealed class AgentAccountException(AgentAccountFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public AgentAccountFailure Failure { get; } = failure;
}
