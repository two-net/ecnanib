namespace Ecnanib.Accounts;

/// <summary>
/// Resetting a forgotten password: a user asks for a link by email, and chooses a new password
/// with it (FR-ACC-2). See docs/accounts.md.
/// </summary>
public interface IPasswordReset
{
    /// <summary>
    /// Sends the link that sets a new password to the account of an address. An address without
    /// an account is sent a notice instead, so every request sends one email and gets the same
    /// answer: the answer does not tell whether an address is registered.
    /// </summary>
    Task<PasswordResetRequestOutcome> RequestAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the password of the account that the token of a reset link belongs to, and ends every
    /// session of that account. The link works once.
    /// </summary>
    /// <param name="token">The token of the link.</param>
    /// <param name="newPassword">
    /// The password to set. It is passed on its own, so that it is never part of a value that can
    /// be printed or logged.
    /// </param>
    /// <param name="cancellationToken">Cancels the reset.</param>
    Task<PasswordResetOutcome> ResetAsync(string token, string newPassword, CancellationToken cancellationToken);
}

/// <summary>How a request for a reset link ended.</summary>
public enum PasswordResetRequestOutcome
{
    /// <summary>The request was taken, and an email is on its way to the address.</summary>
    Accepted,

    /// <summary>What was entered is not an email address, so nothing was done.</summary>
    Invalid,

    /// <summary>The email could not be sent. Asking again sends another.</summary>
    EmailNotSent,
}

/// <summary>How setting a new password with a reset link ended.</summary>
public enum PasswordResetOutcome
{
    /// <summary>The password was set, and every session of the account has ended.</summary>
    Done,

    /// <summary>The token is not one, was used, was replaced by a later link, or has expired.</summary>
    LinkNotValid,

    PasswordTooShort,
    PasswordTooLong,
}

/// <summary>The rules of a password reset link, and the link itself.</summary>
public static class PasswordResetLink
{
    /// <summary>How long a reset link works after it was sent.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>The path of the page that a reset link opens.</summary>
    public const string Path = "/reset-password";

    /// <summary>
    /// The query parameter that carries the token. It is called this so that the one place that
    /// masks secrets in logs recognises it in a logged address. See docs/logging.md.
    /// </summary>
    public const string TokenParameter = "token";

    /// <summary>The link for a token, under the address users reach the service at.</summary>
    public static Uri For(Uri publicUrl, string token)
    {
        ArgumentNullException.ThrowIfNull(publicUrl);
        ArgumentNullException.ThrowIfNull(token);

        return new Uri(publicUrl, $"{Path}?{TokenParameter}={Uri.EscapeDataString(token)}");
    }
}
