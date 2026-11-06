using Ecnanib.Modules.RiskEngine.Contracts;

namespace Ecnanib.Modules.Execution.Contracts;

/// <summary>
/// FR-RISK-09: readies a Futures symbol on the agent's account right before an entry is sent. It requires One-way
/// Mode, sets the symbol's margin type to isolated and its leverage to the entry's. Order execution (#34) calls it
/// before each Futures entry, and sends nothing unless it's <see cref="FuturesSetupOutcome.Ready"/>. It changes the
/// account, so it takes a <see cref="SizedOrder"/>, which only a passing evaluation makes (AC-01), and
/// <c>BannedApis</c> allows it only in Execution and DecisionCycle. See Futures account settings in
/// docs/architecture.md.
/// </summary>
public interface IFuturesSetup
{
    /// <summary>
    /// Reads the account and the symbol, and changes only what differs, so a symbol that's already set up costs two
    /// reads and no change.
    /// </summary>
    /// <param name="symbol">The agent's pair, such as BTCUSDT.</param>
    /// <param name="entry">The entry the engine sized, whose <see cref="SizedOrder.Leverage"/> is set.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="entry"/> isn't a Futures entry with a leverage, or <paramref name="symbol"/> isn't one Binance
    /// would accept. Nothing was sent.
    /// </exception>
    Task<FuturesSetupOutcome> PrepareEntryAsync(
        Guid userId,
        Guid credentialId,
        string symbol,
        SizedOrder entry,
        CancellationToken cancellationToken);
}

/// <summary>Whether a Futures entry may be sent, and if not, why. Stored by name, so a value is never renamed.</summary>
public enum FuturesSetupOutcome
{
    /// <summary>The account is in One-way Mode, and the symbol is isolated at the entry's leverage.</summary>
    Ready,

    /// <summary>The exchange key was deleted or stopped being valid, which its credential's checks deal with.</summary>
    KeyUnusable,

    /// <summary>Binance couldn't be reached, is limiting requests, or refused for a reason of its own. Nothing may have changed.</summary>
    ExchangeUnavailable,

    /// <summary>Binance doesn't let the account trade Futures.</summary>
    TradingDisabled,

    /// <summary>The account is in Hedge Mode, and FR-RISK-09 requires One-way Mode. Only the user can change it.</summary>
    HedgeMode,

    /// <summary>The account is in Multi-Assets Mode, which allows cross margin only, so the symbol can't be isolated.</summary>
    MultiAssetsMode,

    /// <summary>Open orders or a position on the symbol keep its margin type from changing to isolated (Binance's -4047 and -4048).</summary>
    MarginTypeLocked,

    /// <summary>
    /// Binance refused the leverage: it's above the symbol's maximum, or lower than an open isolated position's,
    /// which can't be lowered while it's open.
    /// </summary>
    LeverageRefused,
}
