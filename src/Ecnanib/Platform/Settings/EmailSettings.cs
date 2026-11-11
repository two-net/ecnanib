namespace Ecnanib.Platform.Settings;

/// <summary>The <c>Email</c> section: the mail server that sends the service's email.</summary>
/// <param name="Host">The mail server.</param>
/// <param name="Port">Its port.</param>
/// <param name="User">The account to sign in with, or null when the server needs no sign-in.</param>
/// <param name="Password">The password of <paramref name="User"/>.</param>
/// <param name="From">The sender address.</param>
public sealed record EmailSettings(string Host, int Port, string? User, Secret? Password, string From)
{
    internal static EmailSettings Read(SettingsReader file)
    {
        const string userKey = "Email:User";
        const string passwordKey = "Email:Password";

        var host = file.Text("Email:Host");
        var port = file.WholeNumber("Email:Port", minimum: 1, maximum: 65535);
        var user = file.OptionalText(userKey);
        var password = file.OptionalSecret(passwordKey);
        var from = file.EmailAddress("Email:From");

        // The server takes a sign-in or it does not: both keys are set, or neither is.
        if (file.IsSet(userKey) && !file.IsSet(passwordKey))
        {
            file.Problem(passwordKey, $"is required when {userKey} is set");
        }
        else if (file.IsSet(passwordKey) && !file.IsSet(userKey))
        {
            file.Problem(userKey, $"is required when {passwordKey} is set");
        }

        return new EmailSettings(host, port, user, password, from);
    }
}
