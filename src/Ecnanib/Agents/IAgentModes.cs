using Ecnanib.Credentials;
using Ecnanib.Shared;

namespace Ecnanib.Agents;

/// <summary>
/// The mode of an agent: Demo, which is Binance's test environment, or Live, where it trades
/// with real money. A new agent is in Demo, and its user switches it (FR-AGT-7). See
/// docs/agents.md.
/// </summary>
/// <remarks>
/// <para>
/// Every operation is for the user who is signed in on the request, and reaches the agents of
/// that user only. An agent of another user is not found (NFR-SEC-3).
/// </para>
/// <para>
/// The mode goes by the rule of the market, the symbol, and the timeframe: it is switched only
/// while the agent is in Draft, or is Stopped with no open position or orders (FR-AGT-8). The
/// state of the agent stays as it is. A switch is recorded in the audit trail (NFR-LOG-7).
/// </para>
/// </remarks>
public interface IAgentModes
{
    /// <summary>
    /// What stands in the way of switching an agent to a mode now, apart from what its user
    /// gives with the form: the risk confirmation, and the proof of who they are. A page asks
    /// this before it asks for either.
    /// </summary>
    /// <param name="request">The request of the signed-in user.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="to">The mode that the agent would be switched to.</param>
    /// <returns>
    /// Null when nothing stands in the way. Otherwise how a switch would end now:
    /// <see cref="ModeSwitchOutcome.Switched"/> for an agent that is in the mode already.
    /// </returns>
    Task<ModeSwitchResult?> BlockOfAsync(HttpContext request, Guid agentId, TradingMode to);

    /// <summary>
    /// Switches an agent from Demo to Live. It takes a valid Live Binance credential that may
    /// trade on the market of the agent, the confirmation of the risk, and a fresh proof of who
    /// the user is (FR-AGT-7, FR-ACC-4). The symbol of the agent must be one that its market has
    /// in Live.
    /// </summary>
    /// <param name="request">The request of the signed-in user.</param>
    /// <param name="agentId">The agent.</param>
    /// <param name="riskConfirmed">Whether the user confirmed that they understand the risk of trading with real money.</param>
    Task<ModeSwitchResult> SwitchToLiveAsync(HttpContext request, Guid agentId, bool riskConfirmed);

    /// <summary>
    /// Switches an agent from Live back to Demo. It takes neither a proof nor a credential: no
    /// money is at stake in Demo. The symbol of the agent must be one that its market has there.
    /// </summary>
    /// <param name="request">The request of the signed-in user.</param>
    /// <param name="agentId">The agent.</param>
    Task<ModeSwitchResult> SwitchToDemoAsync(HttpContext request, Guid agentId);
}

/// <summary>How switching the mode of an agent ended.</summary>
public enum ModeSwitchOutcome
{
    /// <summary>The agent is in the mode that was asked for: by this request, or by one before it.</summary>
    Switched,

    /// <summary>The user has no agent of that id.</summary>
    NotFound,

    /// <summary>
    /// The agent is neither in Draft nor Stopped with no open position or orders (FR-AGT-8).
    /// Nothing changed.
    /// </summary>
    StructureLocked,

    /// <summary>The Live Binance credential of the user does not let the agent trade (FR-AGT-7, FR-KEY-5). Nothing changed.</summary>
    KeyBlocks,

    /// <summary>
    /// The exchange did not say which symbols the market has in the mode that was asked for, so
    /// the symbol could not be held to it. Nothing changed.
    /// </summary>
    SymbolsUnknown,

    /// <summary>The market of the agent does not have its symbol in the mode that was asked for (FR-AGT-6). Nothing changed.</summary>
    SymbolNotOnMarket,

    /// <summary>The user has not just proved who they are (FR-ACC-4). Nothing changed.</summary>
    ProofNeeded,

    /// <summary>The user did not confirm the risk of trading with real money (FR-AGT-7). Nothing changed.</summary>
    RiskNotConfirmed,
}

/// <summary>What switching the mode of an agent gave.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Block">What about the Live credential stands in the way, when that is how it ended.</param>
public sealed record ModeSwitchResult(ModeSwitchOutcome Outcome, StartBlock? Block = null);
