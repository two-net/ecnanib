using Ecnanib.Kernel;
using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>
/// An agent's account, as a decision cycle needs it (FR-CYC-02 step 2): the agent's orders are reconciled with Binance
/// first (FR-EXE-06), then come the balances, the symbol's leverage brackets on Futures, and the agent's own position
/// and pending entry, from Execution's records. See Decision cycle and Reconciliation in docs/architecture.md.
/// </summary>
public interface IAgentAccounts
{
    /// <summary>
    /// Reconciles the agent's orders with Binance, then reads the account through the agent's exchange key, now: a cycle
    /// never works from an old balance. When reconciliation can't reach Binance, the position is the records' as they
    /// stand, and execution reconciles again before it sends anything.
    /// </summary>
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
/// The agent's own open position, or null, from its protections: the quantity they hold, their average entry, and the
/// tightest stop, so a stop that passes as tighter never widens any part (FR-RISK-06). On Spot, only what its own fills
/// bought (FR-AGT-09). On Futures, Binance's break-even price when Binance's position is the agent's own.
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
    IReadOnlyList<LeverageBracket> Brackets)
{
    /// <summary>When the position's first part filled. Null without a position.</summary>
    public DateTimeOffset? PositionOpenedAt { get; init; }

    /// <summary>The position's take-profits, nearest first. Its parts can have different ones.</summary>
    public IReadOnlyList<decimal> Targets { get; init; } = [];

    /// <summary>
    /// Futures only: Binance's liquidation price for the position, when Binance's position is the agent's own and
    /// something would liquidate it. Market Data has no keys, so this is where it comes from (FR-CYC-02 step 3).
    /// </summary>
    public decimal? LiquidationPrice { get; init; }

    /// <summary>The entry that hasn't filled yet, or null.</summary>
    public AgentPendingEntry? PendingEntry { get; init; }
}

/// <summary>An entry that rests on Binance unfilled or partly filled, or whose outcome isn't known yet.</summary>
/// <param name="Price">A limit entry's price. Null for a market entry.</param>
/// <param name="Quantity">What it was sent for, in the base asset.</param>
/// <param name="Executed">What has filled so far.</param>
/// <param name="PlacedAt">When it was sent.</param>
public sealed record AgentPendingEntry(PositionSide Side, EntryType Type, decimal? Price, decimal Quantity, decimal Executed, DateTimeOffset PlacedAt);

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
