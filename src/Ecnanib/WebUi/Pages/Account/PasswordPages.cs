using System.Globalization;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the pages of a forgotten password, in one place (NFR-USA-4): the page that asks
/// for a link to choose a new password, the page that says the link was sent, the page that the
/// link opens, and the page that says the password was changed. What these say alike with other
/// pages of the account, such as what a form says of a new password, is in
/// <see cref="AccountPages"/>. See docs/accounts.md.
/// </summary>
public static class PasswordPages
{
    /// <summary>
    /// The title of the page that asks for the link, in the browser, and its heading. The page
    /// that signs in says it too, as the text of the link that leads there.
    /// </summary>
    public static string ForgotTitle => "Forgot your password?";

    /// <summary>What that page does with the address that is entered.</summary>
    public static string ForgotExplained =>
        "Enter the email address of your account. We send a link to it, and with the link you choose a new password.";

    /// <summary>The button that sends the link.</summary>
    public static string SendLink => "Send the link";

    /// <summary>
    /// What the page says after the link was asked for. It says the same whatever the address
    /// was, so that it does not tell whether an address has an account.
    /// </summary>
    public static string SentExplained =>
        "We sent a message to the address you entered. If the address has an account, the message holds a link to choose a new password.";

    /// <summary>How long the link works, and what to do when no message arrives, before the link that asks for a new one.</summary>
    /// <param name="minutes">How many minutes the link works for.</param>
    public static string LinkWorksFor(int minutes) =>
        $"The link works for {minutes.ToString(CultureInfo.InvariantCulture)} minutes. If no message arrives, look in your spam folder, or";

    /// <summary>The text of that link, at the end of the sentence.</summary>
    public static string AskAgainLink => "ask for a new one";

    /// <summary>The title of the page that the link opens, in the browser, and its heading.</summary>
    public static string ResetTitle => "Choose a new password";

    /// <summary>What that page says of a link that does not work.</summary>
    /// <param name="minutes">How many minutes a link works for.</param>
    public static string LinkNotValid(int minutes) =>
        "This link does not work any more. It may have been used, replaced by a later one, or be older than " +
        $"{minutes.ToString(CultureInfo.InvariantCulture)} minutes.";

    /// <summary>The text of the link that asks for a new link, under that.</summary>
    public static string NewLinkLink => "Ask for a new link";

    /// <summary>The label of the field that takes the new password.</summary>
    public static string NewPasswordLabel => "New password";

    /// <summary>What choosing a new password does besides.</summary>
    public static string SignsOut => "Choosing a new password signs your account out on every device.";

    /// <summary>The button that sets the new password.</summary>
    public static string SetPassword => "Set the new password";

    /// <summary>The title of the page that says the password was changed, in the browser, and its heading.</summary>
    public static string DoneTitle => "Your password was changed";

    /// <summary>What that page says.</summary>
    public static string Done => "Your account has a new password, and it was signed out on every device.";

    /// <summary>What stands after the link to the page that signs in.</summary>
    public static string WithNewPassword => "with the new password.";
}
