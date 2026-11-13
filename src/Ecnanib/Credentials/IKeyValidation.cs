using Ecnanib.Shared;

namespace Ecnanib.Credentials;

/// <summary>
/// What the components above Credentials ask about a user's stored keys: to check a key again
/// when a call with it failed authentication (FR-KEY-3), and whether a user's Binance credential
/// lets an agent start on a market (FR-KEY-5). See docs/credentials.md.
/// </summary>
/// <remarks>
/// A method takes the user by id, and not from a request: what calls it works for an agent, in a
/// decision cycle or a job, where nobody is signed in. It takes the id from the row it works on,
/// and never from what came in from outside. See docs/ownership.md.
/// </remarks>
public interface IKeyValidation
{
    /// <summary>
    /// Asks the provider about a user's stored key again, now, and records what it said. Whatever
    /// makes a call with the key calls this when the call fails authentication: a key that the
    /// provider then refuses is invalid, and what depends on it is told through
    /// <see cref="IKeyDependents"/>.
    /// </summary>
    /// <param name="userId">The user whose key it is.</param>
    /// <param name="provider">The provider the key is for.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<KeyCheckOutcome> RevalidateAiKeyAsync(Guid userId, AiProviderCode provider, CancellationToken cancellationToken);

    /// <summary>
    /// Asks Binance about a user's stored credential again, in the same way. A credential that
    /// Binance now reports as able to withdraw is deleted (FR-KEY-4).
    /// </summary>
    /// <param name="userId">The user whose credential it is.</param>
    /// <param name="environment">The environment the credential is for.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<KeyCheckOutcome> RevalidateExchangeKeyAsync(Guid userId, TradingMode environment, CancellationToken cancellationToken);

    /// <summary>
    /// What keeps an agent of a user from starting on a market in an environment, as far as the
    /// user's Binance credential goes, or null when nothing does (FR-KEY-5). It is answered from
    /// what the last check of the credential recorded, and asks Binance nothing.
    /// </summary>
    /// <param name="userId">The user whose agent it is.</param>
    /// <param name="environment">The environment the agent trades in.</param>
    /// <param name="market">The market the agent trades on.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<StartBlock?> BlockForAsync(Guid userId, TradingMode environment, Market market, CancellationToken cancellationToken);
}

/// <summary>What about a user's Binance credential keeps an agent from starting (FR-KEY-5).</summary>
public enum StartBlock
{
    /// <summary>The user has stored no credential for the environment.</summary>
    NoKey,

    /// <summary>The credential is not valid: Binance refused it when it was last asked, or it was never checked.</summary>
    NotValid,

    /// <summary>The credential lacks the permission to trade on the Spot market, which the agent's market needs.</summary>
    SpotTradingMissing,

    /// <summary>The credential lacks the permission to trade on the Futures market, which the agent's market needs.</summary>
    FuturesTradingMissing,
}
