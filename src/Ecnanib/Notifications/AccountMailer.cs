using Ecnanib.Accounts;
using Ecnanib.Platform.Settings;

namespace Ecnanib.Notifications;

/// <summary>
/// Writes the emails that Accounts asks for, and hands them to the email delivery service. See
/// docs/accounts.md.
/// </summary>
internal sealed class AccountMailer(IEmailAdapter email, ApplicationSettings application) : IAccountMailer
{
    public Task SendVerificationLinkAsync(
        string emailAddress, Uri link, bool registeredBefore, CancellationToken cancellationToken)
    {
        var hours = (int)EmailVerification.LinkLifetime.TotalHours;

        var before = registeredBefore
            ? $"""

              This address was registered before and has not been confirmed yet. The password chosen the first time still applies.

              """
            : string.Empty;

        return email.SendAsync(
            new EmailMessage(
                emailAddress,
                $"Confirm your email address for {application.Name}",
                $"""
                Someone registered for {application.Name} with this email address.

                If that was you, open this link to confirm the address:

                {link.AbsoluteUri}

                The link works for {hours} hours.
                {before}
                If it was not you, ignore this message. The address stays unconfirmed, and no agent can be created with the account.
                """),
            cancellationToken);
    }

    public Task SendAlreadyRegisteredNoticeAsync(string emailAddress, CancellationToken cancellationToken) =>
        email.SendAsync(
            new EmailMessage(
                emailAddress,
                $"You already have an account at {application.Name}",
                $"""
                Someone tried to register for {application.Name} with this email address, which already has an account.

                Nothing about your account has changed. If that was you, sign in with the password you chose when you registered.

                If it was not you, you can ignore this message.
                """),
            cancellationToken);
}
