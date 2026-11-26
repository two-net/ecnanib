using System.Globalization;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the pages of two-factor authentication, in one place (NFR-USA-4): the page that
/// turns it on and says how it stands, the page that makes new recovery codes, the page that
/// turns it off, and the parts that show the recovery codes and the QR code. What these say
/// alike with other pages of the account, such as the label of the field of the code and what a
/// form says of a code that it refused, is in <see cref="AccountPages"/>. See docs/accounts.md.
/// </summary>
public static class TwoFactorPages
{
    /// <summary>The title of the page of two-factor authentication, in the browser, and its heading.</summary>
    public static string Title => "Two-factor authentication";

    /// <summary>What that page says when it was just turned on, over the recovery codes.</summary>
    public static string TurnedOn =>
        "Two-factor authentication is on. From now on, signing in takes a code from your authenticator app after your " +
        "password. Your account was signed out on every other device.";

    /// <summary>
    /// What it says when the form that turns it on was sent a second time: the answer to the
    /// first, which showed the recovery codes, may never have been seen.
    /// </summary>
    public static string AlreadyOn =>
        "Two-factor authentication was on already, so nothing was changed. If you did not write down your recovery codes, " +
        "make new ones below.";

    /// <summary>What it says while two-factor authentication is on, with the day it was turned on where that is known.</summary>
    /// <param name="since">When it was turned on, or null when that is not known.</param>
    public static string IsOn(DateTimeOffset? since, PageTimes times) =>
        $"Two-factor authentication is on{(since is { } day ? $", since {times.Day(day)}" : string.Empty)}. " +
        "Signing in takes a code from your authenticator app after your password.";

    /// <summary>How many recovery codes are left, of how many an account is given.</summary>
    /// <param name="left">How many recovery codes are not used yet.</param>
    /// <param name="of">How many recovery codes an account is given.</param>
    public static string CodesLeft(int left, int of) => $"You have {Count(left)} of {Count(of)} recovery codes left.";

    /// <summary>The text of the link to the page that makes new recovery codes.</summary>
    public static string NewCodesLink => "Make new recovery codes";

    /// <summary>
    /// The title of the page that turns two-factor authentication off, in the browser, its
    /// heading, and the text of the link to it.
    /// </summary>
    public static string TurnOffTitle => "Turn off two-factor authentication";

    /// <summary>What the page says in place of its form to a user whose email address is not verified.</summary>
    public static string VerifyFirst =>
        "Two-factor authentication can be turned on once your email address is verified. Open the link in the email you " +
        "were sent when you registered.";

    /// <summary>What two-factor authentication is, over the form that turns it on.</summary>
    public static string Explained =>
        "With two-factor authentication, signing in takes a code from an authenticator app on your phone, after your " +
        "password. Someone who learns your password cannot sign in without your phone.";

    /// <summary>What the form answers when the key it carried cannot be used: it shows a new one.</summary>
    public static string KeyNotValid => "This page could not be used any more. Here is a new key: scan it, and enter the code it gives.";

    /// <summary>The first step of turning it on: scanning the QR code.</summary>
    public static string Scan => "Open your authenticator app and scan this QR code.";

    /// <summary>What to do when the QR code cannot be scanned, before the key itself.</summary>
    public static string TypeKey => "If you cannot scan it, type this key into the app instead:";

    /// <summary>The second step: the code and the password.</summary>
    public static string EnterCode => "Enter the code that the app shows, and your password.";

    /// <summary>What that form says under a code that it refused. It takes no recovery code.</summary>
    public static string WrongCode => "That code is not right. Enter the six digits that the app shows now.";

    /// <summary>What turning it on does besides.</summary>
    public static string SignsOut => "Turning it on signs your account out on every other device.";

    /// <summary>The button that turns two-factor authentication on.</summary>
    public static string TurnOn => "Turn on two-factor authentication";

    /// <summary>The title of the page that makes new recovery codes, in the browser, and its heading.</summary>
    public static string NewCodesTitle => "New recovery codes";

    /// <summary>What that page says when the new codes were made, over them.</summary>
    public static string CodesReplaced => "Your recovery codes were replaced. The earlier ones no longer work.";

    /// <summary>The text of the link that leads from there back to the page of two-factor authentication.</summary>
    public static string BackLink => "Back to two-factor authentication";

    /// <summary>What making new recovery codes does, over the form.</summary>
    /// <param name="count">How many recovery codes are made.</param>
    public static string MakesNewCodes(int count) =>
        $"This makes {Count(count)} new recovery codes. The ones you have now stop working, used or not.";

    /// <summary>The button that makes new recovery codes.</summary>
    public static string MakeNewCodes => "Make new recovery codes";

    /// <summary>The text of the link that leads back without making new codes.</summary>
    public static string KeepCodesLink => "Keep the ones you have";

    /// <summary>What turning two-factor authentication off does, over the form.</summary>
    public static string TurnOffExplained =>
        "After this, signing in takes your password alone, and your recovery codes stop working. You can turn it on again " +
        "at any time, which is also how to move to another phone.";

    /// <summary>The button that turns two-factor authentication off.</summary>
    public static string TurnOff => "Turn off two-factor authentication";

    /// <summary>The text of the link that leads back without turning it off.</summary>
    public static string KeepOnLink => "Keep it on";

    /// <summary>The heading of the recovery codes, on the one page view that shows them.</summary>
    public static string CodesHeading => "Your recovery codes";

    /// <summary>What the recovery codes are for, and where to keep them.</summary>
    public static string KeepCodesSafe =>
        "Keep these codes somewhere safe, away from your phone. If you lose your authenticator app, each of them signs you " +
        "in once in its place.";

    /// <summary>That the recovery codes are shown once, in bold, under them.</summary>
    public static string ShownOnlyNow => "They are shown only now.";

    /// <summary>What to do with them before leaving the page, after that.</summary>
    public static string WriteThemDown => "Write them down or print this page before you leave it.";

    /// <summary>What the QR code is, as a screen reader says it.</summary>
    public static string QrCodeLabel => "QR code to scan with your authenticator app";

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}
