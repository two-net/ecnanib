namespace Ecnanib.Accounts;

/// <summary>
/// Signing in and out: a user proves who they are with their email address and password, and
/// their browser is given a session until they sign out or stay away too long (FR-ACC-2,
/// NFR-SEC-1). See docs/accounts.md.
/// </summary>
public interface ISignIn
{
    /// <summary>
    /// Signs the browser of a request in to the account of an address, when the password is the
    /// account's. A session the browser already holds ends.
    /// </summary>
    /// <param name="request">The request that posted the sign-in form. Its response gets the session cookie.</param>
    /// <param name="email">The email address, as typed.</param>
    /// <param name="password">The password.</param>
    /// <returns>
    /// False when the address has no account or the password is not its password. Which of the
    /// two it was is not told, and neither takes longer than the other.
    /// </returns>
    Task<bool> SignInAsync(HttpContext request, string email, string password);

    /// <summary>
    /// Ends the session of the browser of a request, in the database and in the browser. A
    /// request without a session is left as it is.
    /// </summary>
    Task SignOutAsync(HttpContext request);
}
