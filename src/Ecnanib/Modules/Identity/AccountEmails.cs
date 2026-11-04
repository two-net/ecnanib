using System.Globalization;
using Ecnanib.Platform.Contracts;

namespace Ecnanib.Modules.Identity;

/// <summary>
/// The links and messages accounts send. A token goes in the query string as token=, never in the
/// path, so the masking rules can see it wherever a URL ends up. Links start with App:PublicBaseUrl,
/// never the request's Host, which a client chooses and could point at a site of its own.
/// </summary>
internal sealed class AccountEmails(IdentitySettings settings)
{
    /// <summary>The page that verifies an address. Components/Account/VerifyEmail.razor.</summary>
    public const string VerifyEmailPath = "/account/verify-email";

    /// <summary>The page that sets a new password. Components/Account/ResetPassword.razor.</summary>
    public const string ResetPasswordPath = "/reset-password";

    public EmailMessage Verification(string to, string token, DateTimeOffset expiresAt) => new()
    {
        To = to,
        Subject = Emails.VerificationSubject,
        TextBody = string.Format(CultureInfo.InvariantCulture, Emails.VerificationBody, Link(VerifyEmailPath, token), expiresAt),
    };

    public EmailMessage PasswordReset(string to, string token, DateTimeOffset expiresAt) => new()
    {
        To = to,
        Subject = Emails.PasswordResetSubject,
        TextBody = string.Format(CultureInfo.InvariantCulture, Emails.PasswordResetBody, Link(ResetPasswordPath, token), expiresAt),
    };

    /// <summary>A path alone only without App:PublicBaseUrl, which happens in Development with email off.</summary>
    public string Link(string path, string token) => $"{settings.PublicOrigin}{path}?token={token}";
}
