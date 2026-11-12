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

              This address was registered before and has not been confirmed yet. When you confirm it with this link, you choose a new password, and a password chosen earlier stops working.

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

    public Task SendPasswordResetLinkAsync(string emailAddress, Uri link, CancellationToken cancellationToken)
    {
        var minutes = (int)PasswordResetLink.Lifetime.TotalMinutes;

        return email.SendAsync(
            new EmailMessage(
                emailAddress,
                $"Choose a new password for {application.Name}",
                $"""
                Someone asked to reset the password of your account at {application.Name}.

                If that was you, open this link to choose a new password:

                {link.AbsoluteUri}

                The link works for {minutes} minutes, and once. Choosing a new password signs the account out everywhere.

                If it was not you, ignore this message. Your password stays as it is.
                """),
            cancellationToken);
    }

    public Task SendNoAccountNoticeAsync(string emailAddress, CancellationToken cancellationToken) =>
        email.SendAsync(
            new EmailMessage(
                emailAddress,
                $"No account at {application.Name} with this address",
                $"""
                Someone asked to reset the password of an account at {application.Name} with this email address, which has no account.

                If that was you, you may have registered with another address.

                If it was not you, you can ignore this message.
                """),
            cancellationToken);

    public Task SendPasswordChangedNoticeAsync(string emailAddress, CancellationToken cancellationToken) =>
        email.SendAsync(
            new EmailMessage(
                emailAddress,
                $"Your password at {application.Name} was changed",
                $"""
                The password of your account at {application.Name} was changed with a link sent to this email address, and the account was signed out everywhere.

                If that was you, there is nothing to do.

                If it was not you, someone else can read your email. Secure your mailbox first, and then ask for a new password on the sign-in page.
                """),
            cancellationToken);

    public Task SendAccountDeletedNoticeAsync(string emailAddress, CancellationToken cancellationToken) =>
        email.SendAsync(
            new EmailMessage(
                emailAddress,
                $"Your account at {application.Name} was deleted",
                $"""
                Your account at {application.Name} was deleted, with everything that belonged to it. This is the last message to this email address, which {application.Name} no longer keeps.

                If that was you, there is nothing to do. You can register again at any time.

                If it was not you, someone else knew how to sign in to your account. If you use its password anywhere else, change it there.
                """),
            cancellationToken);
}
