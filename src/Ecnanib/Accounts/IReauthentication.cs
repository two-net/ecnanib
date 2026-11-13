using Ecnanib.Platform.Persistence;

namespace Ecnanib.Accounts;

/// <summary>
/// An action that exposes keys or money. It is done only for a user who has just proved who they
/// are again (FR-ACC-4): being signed in is not enough for it. Whatever does one of these asks
/// <see cref="IReauthentication.IsRecentAsync(HttpContext, SensitiveAction)"/> first.
/// </summary>
public enum SensitiveAction
{
    /// <summary>Storing a key of an AI provider or of Binance, as a new one or in place of the one that is stored.</summary>
    AddOrReplaceKey,

    /// <summary>Switching an agent from Demo to Live, where it trades with real money.</summary>
    SwitchAgentToLive,

    /// <summary>Deleting the account.</summary>
    DeleteAccount,
}

/// <summary>The rules of re-authentication that a page tells its user about. See docs/accounts.md.</summary>
public static class ReauthenticationRules
{
    /// <summary>
    /// How long a proof holds, on the browser that gave it. A sensitive action that comes later
    /// asks for another.
    /// </summary>
    public static readonly TimeSpan HoldsFor = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Re-authentication: before a sensitive action, a user who is signed in proves again who they
/// are, with what signing in takes: their password, and a code as well when they have two-factor
/// authentication on (FR-ACC-4). The proof holds for <see cref="ReauthenticationRules.HoldsFor"/>,
/// and only for the browser that gave it. See docs/accounts.md.
/// </summary>
public interface IReauthentication
{
    /// <summary>
    /// Says whether the user of a request proved who they are again, on this browser and recently
    /// enough for a sensitive action. Whatever does such an action asks this first, on the
    /// server, and does nothing when the answer is no. A page then sends the browser to
    /// <see cref="SessionAuthentication.ReauthenticationPath"/>.
    /// </summary>
    /// <param name="request">The request that asks for the action.</param>
    /// <param name="action">The action that is asked for.</param>
    /// <returns>
    /// False when the proof is too long ago or was never given: signing in does not count as one.
    /// Also false for a request that is not signed in.
    /// </returns>
    Task<bool> IsRecentAsync(HttpContext request, SensitiveAction action);

    /// <summary>
    /// The same question, asked inside the transaction that does the action, after it has locked
    /// the row of the account. The answer then holds until the action is done: whatever ends the
    /// proof, such as a new password, changes that row, so it came before or waits.
    /// </summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="request">The request that asks for the action.</param>
    /// <param name="action">The action that is asked for.</param>
    Task<bool> IsRecentAsync(EcnanibDbContext context, HttpContext request, SensitiveAction action);

    /// <summary>
    /// Where the user of a request stands, for the page that takes the proof. It fails for a
    /// request that is not signed in.
    /// </summary>
    Task<ReauthenticationState> StateAsync(HttpContext request);

    /// <summary>
    /// Takes the proof of the user who is signed in on a request, and when it is right writes
    /// down that the browser of the request gave it. It fails for a request that is not signed in.
    /// </summary>
    /// <param name="request">The request that posted the form.</param>
    /// <param name="password">
    /// The password of the account. It is passed on its own, so that it is never part of a value
    /// that can be printed or logged.
    /// </param>
    /// <param name="code">
    /// A code from the authenticator app, or a recovery code. It is looked at only for an account
    /// with two-factor authentication.
    /// </param>
    Task<ReauthenticationOutcome> ConfirmAsync(HttpContext request, string password, string code);
}

/// <summary>Where a user stands with proving who they are again.</summary>
/// <param name="AsksForCode">
/// Whether the proof is a code besides the password: the account has two-factor authentication on.
/// </param>
/// <param name="IsRecent">Whether a proof was given on this browser, recently enough for a sensitive action.</param>
public sealed record ReauthenticationState(bool AsksForCode, bool IsRecent);

/// <summary>How giving the proof ended.</summary>
public enum ReauthenticationOutcome
{
    /// <summary>The user proved who they are. Sensitive actions are done for them for a while, on this browser.</summary>
    Confirmed,

    /// <summary>
    /// The password is not the password of the account. For an account with two-factor
    /// authentication this is said only after a code that was right, which is spent by it.
    /// </summary>
    WrongPassword,

    /// <summary>The code is not right. Whether the password was is not said.</summary>
    WrongCode,

    /// <summary>
    /// Every code of the account is refused for <see cref="TwoFactorRules.RefusedFor"/>, after
    /// too many wrong ones.
    /// </summary>
    CodesRefused,

    /// <summary>The session of the browser ended while the proof was looked at. The user signs in again.</summary>
    SessionEnded,

    /// <summary>
    /// The password of the account was tried too often by browsers that are signed in to it
    /// (NFR-SEC-4). Neither the password nor the code was looked at, and no code is spent.
    /// </summary>
    TooManyAttempts,
}
