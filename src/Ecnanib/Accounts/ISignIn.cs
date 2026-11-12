namespace Ecnanib.Accounts;

/// <summary>
/// Signing in and out: a user proves who they are with their email address and password, and
/// their browser is given a session until they sign out or stay away too long (FR-ACC-2,
/// NFR-SEC-1). A user who has two-factor authentication on proves it with a code as well, before
/// the session starts (FR-ACC-3). See docs/accounts.md.
/// </summary>
public interface ISignIn
{
    /// <summary>
    /// Signs the browser of a request in to the account of an address, when the password is the
    /// account's. A session the browser already holds ends. For an account with two-factor
    /// authentication no session starts yet: the browser is given a sign-in that waits for a
    /// code, which <see cref="CompleteAsync"/> takes.
    /// </summary>
    /// <param name="request">The request that posted the sign-in form. Its response gets the cookie.</param>
    /// <param name="email">The email address, as typed.</param>
    /// <param name="password">The password.</param>
    /// <returns>
    /// <see cref="SignInOutcome.Failed"/> when the address has no account or the password is not
    /// its password. Which of the two it was is not told, and neither takes longer than the other.
    /// </returns>
    Task<SignInOutcome> SignInAsync(HttpContext request, string email, string password);

    /// <summary>
    /// Whether the browser of a request gave the password of an account with two-factor
    /// authentication, and can still give the code.
    /// </summary>
    Task<bool> AwaitsCodeAsync(HttpContext request);

    /// <summary>
    /// Signs the browser of a request in with a code, after <see cref="SignInAsync"/> asked for
    /// one. The session starts only with a code that is right, and a code is accepted once.
    /// </summary>
    /// <param name="request">The request that posted the form with the code. Its response gets the session cookie.</param>
    /// <param name="code">A code from the authenticator app, or a recovery code.</param>
    Task<SignInCodeOutcome> CompleteAsync(HttpContext request, string code);

    /// <summary>
    /// Ends the session of the browser of a request, in the database and in the browser, and a
    /// sign-in of the browser that waits for a code. A request without either is left as it is.
    /// </summary>
    Task SignOutAsync(HttpContext request);
}

/// <summary>How giving an email address and a password ended.</summary>
public enum SignInOutcome
{
    /// <summary>The address has no account, or the password is not its password. Nobody is signed in.</summary>
    Failed,

    /// <summary>The browser is signed in.</summary>
    SignedIn,

    /// <summary>
    /// The password is right, and the account has two-factor authentication. Nobody is signed in
    /// until the browser gives a code.
    /// </summary>
    CodeNeeded,
}

/// <summary>How giving a code after the password ended.</summary>
public enum SignInCodeOutcome
{
    /// <summary>The browser is signed in.</summary>
    SignedIn,

    /// <summary>The code is not right. Another can be tried.</summary>
    WrongCode,

    /// <summary>
    /// Every code of the account is refused for <see cref="TwoFactorRules.RefusedFor"/>, after
    /// too many wrong ones.
    /// </summary>
    CodesRefused,

    /// <summary>
    /// No sign-in of the browser waits for a code: it waited too long, the password was changed,
    /// or two-factor authentication was turned off. Signing in starts again with the password.
    /// </summary>
    StartAgain,
}
