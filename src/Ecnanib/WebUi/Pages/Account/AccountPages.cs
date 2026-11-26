using System.Globalization;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the pages of an account, in one place (NFR-USA-4): the page of the account
/// itself, the page that deletes it, and the page that says it is gone. What several pages of
/// the account say alike is here too, once: the label of a field, what a form answers after too
/// many attempts, what it says of a password or a code that it refused, that an email was sent,
/// and the links back to the account and on to signing in. The pages of signing in, of
/// registering, of the password, of two-factor authentication, and of the keys have classes of
/// their own beside this one. See docs/accounts.md.
/// </summary>
public static class AccountPages
{
    /// <summary>The title of the page of the account in the browser, and its heading.</summary>
    public static string Title => "Your account";

    /// <summary>Whose account it is, by the email address of the user who is signed in.</summary>
    public static string SignedInAs(string? emailAddress) => $"You are signed in as {emailAddress}.";

    /// <summary>The heading of what the page says of two-factor authentication.</summary>
    public static string TwoFactorHeading => "Two-factor authentication";

    /// <summary>What it says while two-factor authentication is on, with how many recovery codes are left.</summary>
    /// <param name="left">How many recovery codes are not used yet.</param>
    /// <param name="of">How many recovery codes an account is given.</param>
    public static string TwoFactorOn(int left, int of) =>
        $"On. Signing in takes a code from your authenticator app after your password. {TwoFactorPages.CodesLeft(left, of)}";

    /// <summary>The text of the link to the page of two-factor authentication, while it is on.</summary>
    public static string ManageTwoFactorLink => "Manage two-factor authentication";

    /// <summary>What the page says while two-factor authentication is off.</summary>
    public static string TwoFactorOff => "Off. Signing in takes your password alone.";

    /// <summary>The text of the link to the page of two-factor authentication, while it is off.</summary>
    public static string TurnOnTwoFactorLink => "Turn on two-factor authentication";

    /// <summary>The heading of what the page says of the keys.</summary>
    public static string KeysHeading => "API keys";

    /// <summary>Whose keys the agents work with, and how a key is kept.</summary>
    public static string KeysExplained =>
        "Your agents work with your own keys of the AI providers and of Binance. A key is stored encrypted, and after saving " +
        "you see only its last four characters.";

    /// <summary>The text of the link to the page of the keys.</summary>
    public static string KeysLink => "Manage your API keys";

    /// <summary>The heading of what the page says of the notifications.</summary>
    public static string NotificationsHeading => "Notifications";

    /// <summary>Where the service tells a user what matters, before the link to that page.</summary>
    public static string NotificationsExplained =>
        "The service tells you what matters about your agents and your keys on the page of your";

    /// <summary>The text of the link to the page of the notifications, at the end of that sentence.</summary>
    public static string NotificationsLink => "notifications";

    /// <summary>What the page says to a user who is sent email for critical events.</summary>
    public static string EmailOn => "Email for critical events: on. Your choice is stored with your account, and is in a download.";

    /// <summary>The text of the link to the page of that choice, while the email is on.</summary>
    public static string ManageEmailLink => "Manage email for critical events";

    /// <summary>What the page says to a user who is sent no email for critical events.</summary>
    public static string EmailOff => "Email for critical events: off. Your choice is stored with your account, and is in a download.";

    /// <summary>The text of the link to the page of that choice, while the email is off.</summary>
    public static string TurnOnEmailLink => "Turn on email for critical events";

    /// <summary>The heading of what the page says of the appearance.</summary>
    public static string AppearanceHeading => "Appearance";

    /// <summary>The text of the link to the page of the appearance.</summary>
    public static string AppearanceLink => "Choose how the pages look";

    /// <summary>The heading of what the page says of the data of its user.</summary>
    public static string DataHeading => "Your data";

    /// <summary>What is stored about a user, and what of it is in no download.</summary>
    public static string DataExplained =>
        "What is stored about you is your email address, when you registered and verified it, the legal documents you accepted, " +
        "the devices you are signed in on, the security-relevant actions of your account, which keys you stored, and what the " +
        "service told you. Your password is stored only as a hash, and a key only encrypted: neither is in a download, which " +
        "names a key by its last four characters.";

    /// <summary>Where the closed trades of a user's agents are counted, before the link to the board.</summary>
    public static string BoardCounts => "The closed trades of your agents are counted on the";

    /// <summary>The text of the link to the board, inside that sentence.</summary>
    public static string BoardLink => "platform-wide board";

    /// <summary>What the board is, and where a user leaves it, after that link and a comma.</summary>
    public static string BoardExplained =>
        "which adds up the results of all users and names none of them. You can leave the board there. Whether you left it is " +
        "stored with your account, and is in a download.";

    /// <summary>The text of the link that downloads what is stored about a user.</summary>
    public static string DownloadLink => "Download your data";

    /// <summary>
    /// The title of the page that deletes an account in the browser, and its heading. The page
    /// of the account says the same over what it says of deleting, and as the text of the link
    /// that leads there.
    /// </summary>
    public static string DeleteTitle => "Delete your account";

    /// <summary>What deleting an account does, as the page of the account says it.</summary>
    public static string DeleteExplained =>
        "Deleting your account removes it and everything that belongs to it, for good. You are asked to confirm that it is you first.";

    /// <summary>What the page that deletes an account answers while an agent of its user is not stopped.</summary>
    public static string AgentsNotStopped => "One of your agents is not stopped. Stop every agent first, and then delete your account.";

    /// <summary>What that page says over the list of what is deleted.</summary>
    public static string Deletes => "This deletes your account and everything that belongs to it, at once and for good:";

    /// <summary>The first of what is deleted: what a user signs in with.</summary>
    public static string DeletesAddress => "your email address and your password,";

    /// <summary>The second: two-factor authentication.</summary>
    public static string DeletesTwoFactor => "two-factor authentication and its recovery codes,";

    /// <summary>The third: the keys.</summary>
    public static string DeletesKeys => "the keys of the AI providers and of Binance that you stored,";

    /// <summary>The fourth: which legal documents were accepted.</summary>
    public static string DeletesDocuments => "the record of the legal documents you accepted,";

    /// <summary>The fifth: what was done with the account.</summary>
    public static string DeletesActions => "the record of the security-relevant actions of your account,";

    /// <summary>The last: the sign-in on every device.</summary>
    public static string DeletesSignIns => "and your sign-in on this and on every other device.";

    /// <summary>That deleting cannot be undone, before the link that downloads what is stored.</summary>
    public static string CannotBeUndone => "It cannot be undone. If you want a copy of what is stored about you,";

    /// <summary>The text of that link, inside the sentence.</summary>
    public static string DownloadFirstLink => "download your data";

    /// <summary>The end of that sentence, after the link.</summary>
    public static string DownloadFirst => "first.";

    /// <summary>The button that deletes the account.</summary>
    public static string Delete => "Delete my account";

    /// <summary>The text of the link that leads back to the account, and deletes nothing.</summary>
    public static string KeepLink => "Keep my account";

    /// <summary>The title, in the browser, of the page that says an account is gone, and its heading.</summary>
    public static string DeletedTitle => "Your account was deleted";

    /// <summary>What that page says: what is gone, and that a last message was sent.</summary>
    public static string DeletedExplained =>
        "Your account and everything that belonged to it are gone, and you were signed out on every device. A last message was " +
        "sent to your email address to confirm it.";

    /// <summary>The text of the link that leads from there to the start page.</summary>
    public static string BackToStart => "Back to the start page";

    /// <summary>The text of the link that leads from a page of the account back to the account.</summary>
    public static string BackToAccount => "Back to your account";

    /// <summary>
    /// The title, in the browser, of a page that says an email with a link was sent, and its
    /// heading: after registering, and after asking for a new password.
    /// </summary>
    public static string CheckYourEmail => "Check your email";

    /// <summary>The text of the link to the page that signs in, from a page that a visitor is done with.</summary>
    public static string SignInLink => "Sign in";

    /// <summary>The label of the field that takes an email address.</summary>
    public static string EmailLabel => "Email address";

    /// <summary>The label of the field that takes a password, where a visitor signs in or chooses one.</summary>
    public static string PasswordLabel => "Password";

    /// <summary>The label of the field that takes the password of the user who is signed in.</summary>
    public static string YourPasswordLabel => "Your password";

    /// <summary>What a form answers after too many attempts. It says the same whatever was entered.</summary>
    /// <param name="minutes">How many minutes the limit counts attempts over.</param>
    public static string TooManyAttempts(int minutes) => $"Too many attempts. Try again in up to {Count(minutes)} minutes.";

    /// <summary>What a form answers when the email it asked for could not be sent.</summary>
    public static string EmailNotSent => "We could not send the email. Try again in a few minutes.";

    /// <summary>What a form says under an email address that is none.</summary>
    public static string EnterEmail => "Enter an email address, such as name@example.com.";

    /// <summary>The hint under a field that takes a new password.</summary>
    /// <param name="minimumLength">How many characters a password has at least.</param>
    public static string PasswordHint(int minimumLength) => $"At least {Count(minimumLength)} characters. A long phrase is a good password.";

    /// <summary>What a form says under a new password that is too short.</summary>
    /// <param name="minimumLength">How many characters a password has at least.</param>
    public static string PasswordTooShort(int minimumLength) => $"Choose a password of at least {Count(minimumLength)} characters.";

    /// <summary>What a form says under a new password that is too long.</summary>
    /// <param name="maximumLength">How many characters a password has at most.</param>
    public static string PasswordTooLong(int maximumLength) => $"Choose a password of at most {Count(maximumLength)} characters.";

    /// <summary>What a form says under a password that is not the one of the user who is signed in.</summary>
    public static string NotYourPassword => "That is not your password.";

    /// <summary>The label of the field that takes a code of two-factor authentication.</summary>
    public static string CodeLabel => "Code from your authenticator app";

    /// <summary>The hint under that field, where a recovery code is taken as well.</summary>
    public static string CodeHint => "If you have lost the app, enter one of your recovery codes instead.";

    /// <summary>What a form says under a code that it refused.</summary>
    public static string CodeNotRight => "That code is not right. A code from the app works once, for about half a minute.";

    /// <summary>What a form answers after too many wrong codes.</summary>
    /// <param name="minutes">How many minutes no code is accepted for.</param>
    public static string CodesRefused(int minutes) =>
        $"Too many wrong codes were entered. No code is accepted for the next {Count(minutes)} minutes.";

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}
