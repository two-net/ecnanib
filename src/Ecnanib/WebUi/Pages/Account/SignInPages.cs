using System.Globalization;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the pages of signing in, in one place (NFR-USA-4): the page that takes the
/// email address and the password, the page that takes the code of two-factor authentication
/// after them, and the page where a user who is signed in confirms that it is them before a
/// sensitive action. What these say alike with other pages of the account, such as the labels of
/// the fields, is in <see cref="AccountPages"/>. See docs/accounts.md.
/// </summary>
public static class SignInPages
{
    /// <summary>The title of the page that signs in, in the browser, and its heading.</summary>
    public static string Title => "Sign in";

    /// <summary>
    /// What the page answers when it did not sign in: one answer whatever was wrong, so that it
    /// does not tell whether an address has an account.
    /// </summary>
    public static string NotRight => "The email address or the password is not right.";

    /// <summary>The button that signs in, on the page of the password and on the page of the code.</summary>
    public static string SignIn => "Sign in";

    /// <summary>The title of the page that takes the code of two-factor authentication, and its heading.</summary>
    public static string CodeTitle => "Enter your code";

    /// <summary>What that page answers after too many wrong codes: no code is taken for a while, and then the user signs in anew.</summary>
    /// <param name="minutes">How many minutes no code is accepted for.</param>
    public static string CodesRefused(int minutes) => $"{AccountPages.CodesRefused(minutes)} Then sign in again.";

    /// <summary>The text of the link that leads from that page back to the page of the password.</summary>
    public static string StartAgainLink => "Start again with your password";

    /// <summary>The title of the page where a user confirms that it is them, and its heading.</summary>
    public static string ConfirmTitle => "Confirm that it is you";

    /// <summary>What that page is for, and how long a confirmation holds.</summary>
    /// <param name="minutes">How many minutes a confirmation holds for.</param>
    public static string ConfirmExplained(int minutes) =>
        "Adding or replacing a key, switching an agent to Live, and deleting your account are done only after you have " +
        $"confirmed that it is you. It then holds for {minutes.ToString(CultureInfo.InvariantCulture)} minutes, on this browser.";

    /// <summary>
    /// What that page says under a password that is not the one of the user. Where it asks for a
    /// code as well, it says that the code was used up by the try.
    /// </summary>
    /// <param name="asksForCode">Whether the page asks for a code of two-factor authentication.</param>
    public static string NotYourPassword(bool asksForCode) =>
        asksForCode
            ? $"{AccountPages.NotYourPassword} The code was used up by this try: enter your password with the next code that the " +
              "app shows, or with another recovery code."
            : AccountPages.NotYourPassword;

    /// <summary>The button that confirms that it is the user.</summary>
    public static string Confirm => "Confirm";
}
