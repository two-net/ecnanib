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
    /// that the earlier password still applies.
    /// </param>
    /// <param name="cancellationToken">Cancels the sending.</param>
    Task SendVerificationLinkAsync(string emailAddress, Uri link, bool registeredBefore, CancellationToken cancellationToken);

    /// <summary>
    /// Tells the owner of a verified account that someone tried to register with their address,
    /// and that nothing has changed.
    /// </summary>
    Task SendAlreadyRegisteredNoticeAsync(string emailAddress, CancellationToken cancellationToken);
}
