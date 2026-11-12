namespace Ecnanib.Accounts;

/// <summary>
/// The emails that Accounts needs sent. Accounts depends on no other component, so it declares
/// this interface, and Notifications implements it. See "When a lower component must trigger a
/// higher one" in docs/architecture.md.
/// </summary>
/// <remarks>
/// A method returns when the email delivery service has taken the message, and throws when it
/// has not.
/// </remarks>
public interface IAccountMailer
{
    /// <summary>Sends the link that verifies an email address.</summary>
    /// <param name="emailAddress">The address to verify.</param>
    /// <param name="link">The verification link. It is a secret until it is used.</param>
    /// <param name="registeredBefore">
    /// True when the address already had an account that is not verified, so the message says
    /// that confirming the link means choosing a new password.
    /// </param>
    /// <param name="cancellationToken">Cancels the sending.</param>
    Task SendVerificationLinkAsync(string emailAddress, Uri link, bool registeredBefore, CancellationToken cancellationToken);

    /// <summary>
    /// Tells the owner of a verified account that someone tried to register with their address,
    /// and that nothing has changed.
    /// </summary>
    Task SendAlreadyRegisteredNoticeAsync(string emailAddress, CancellationToken cancellationToken);

    /// <summary>Sends the link that sets a new password for an account (FR-ACC-2).</summary>
    /// <param name="emailAddress">The address of the account.</param>
    /// <param name="link">The reset link. It is a secret until it is used.</param>
    /// <param name="cancellationToken">Cancels the sending.</param>
    Task SendPasswordResetLinkAsync(string emailAddress, Uri link, CancellationToken cancellationToken);

    /// <summary>
    /// Tells the owner of an address that someone asked to reset the password of an account with
    /// it, and that it has no account.
    /// </summary>
    Task SendNoAccountNoticeAsync(string emailAddress, CancellationToken cancellationToken);

    /// <summary>Tells the owner of an account that its password was changed with a reset link.</summary>
    Task SendPasswordChangedNoticeAsync(string emailAddress, CancellationToken cancellationToken);

    /// <summary>
    /// Tells the owner of an address that the account with it was deleted (FR-ACC-9). It is the
    /// last message to the address, which is kept nowhere once this is sent.
    /// </summary>
    Task SendAccountDeletedNoticeAsync(string emailAddress, CancellationToken cancellationToken);
}
