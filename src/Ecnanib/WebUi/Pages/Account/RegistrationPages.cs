using System.Globalization;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the pages of registering, in one place (NFR-USA-4): the page that registers a
/// visitor, the page that says the email to confirm the address was sent, and the page that the
/// link in it opens. What these say alike with other pages of the account, such as the labels of
/// the fields and what a form says of a password, is in <see cref="AccountPages"/>. The names of
/// the legal documents are in <c>LegalPages</c>. See docs/accounts.md.
/// </summary>
public static class RegistrationPages
{
    /// <summary>The title of the page that registers a visitor, in the browser, and its heading.</summary>
    public static string Title => "Register";

    /// <summary>What that page says in place of its form while registration is closed.</summary>
    public static string Closed => "Registration is closed. No new accounts are being accepted at the moment.";

    /// <summary>What the legal documents that a visitor accepts are, on the form.</summary>
    public static string DocumentsLegend => "Before you register";

    /// <summary>
    /// What the box of a legal document says before the link to it, with the blank before it. The
    /// blank is of the text, and not of the page: the label begins with it, and a page writes no
    /// blank where an element begins.
    /// </summary>
    public static string Accept => " I have read and accept the";

    /// <summary>Which version of a legal document a visitor accepts, as the box says it after the link, between brackets.</summary>
    /// <param name="version">The version of the document.</param>
    public static string Version(string version) => $"version {version}";

    /// <summary>What a form says under a legal document that the visitor did not accept.</summary>
    /// <param name="document">The name of the document, as users read it.</param>
    public static string AcceptCurrent(string document) => $"Read and accept the current {document} to register.";

    /// <summary>The button that registers.</summary>
    public static string Register => "Register";

    /// <summary>What the page says after registering, whatever the address was: that a message was sent, and what to do with it.</summary>
    public static string SentExplained =>
        "We sent a message to the address you entered. Open the link in it to confirm that the address is yours.";

    /// <summary>How long the link works, and what to do when no message arrives, before the link that registers again.</summary>
    /// <param name="hours">How many hours the link works for.</param>
    public static string LinkWorksFor(int hours) =>
        $"The link works for {Count(hours)} hours. If no message arrives, look in your spam folder, or";

    /// <summary>The text of that link, inside the sentence.</summary>
    public static string RegisterAgainLink => "register again";

    /// <summary>The end of that sentence, after the link.</summary>
    public static string GetNewOne => "to get a new one.";

    /// <summary>The title of the page that the link opens, in the browser, and its heading.</summary>
    public static string VerifyTitle => "Confirm your email address";

    /// <summary>What that page says when the address is confirmed.</summary>
    public static string Confirmed => "Your email address is confirmed.";

    /// <summary>What that page says of a link that does not work.</summary>
    /// <param name="hours">How many hours a link works for.</param>
    public static string LinkNotValid(int hours) =>
        $"This link does not work any more. It may have been used, replaced by a later one, or be older than {Count(hours)} hours.";

    /// <summary>The text of the link that registers again, under that, at the start of the sentence.</summary>
    public static string RegisterAgain => "Register again";

    /// <summary>The end of that sentence, after the link.</summary>
    public static string GetNewLink => "to get a new link.";

    /// <summary>What that page asks before the button is pressed.</summary>
    public static string ConfirmToFinish => "Confirm that this email address is yours to finish registering.";

    /// <summary>
    /// What it says when the link is of an address that was registered more than once: whoever
    /// reads its mail chooses the password.
    /// </summary>
    public static string RegisteredAgain =>
        "This address was registered more than once before it was confirmed. Choose the password of your account now. " +
        "A password chosen earlier stops working.";

    /// <summary>The button that confirms the address.</summary>
    public static string Confirm => "Confirm my email address";

    private static string Count(int count) => count.ToString(CultureInfo.InvariantCulture);
}
