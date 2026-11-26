using Ecnanib.Accounts;

namespace Ecnanib.WebUi.Pages.Operator;

/// <summary>
/// The texts of the pages on which an operator disables and enables the account of a user, in
/// one place (NFR-USA-4): the page that finds an account by its email address, and the page of
/// the account that was found (FR-OPS-4). The operator status screen leads to the first, and the
/// first to the second. See "Operators" in docs/ownership.md.
/// </summary>
public static class OperatorAccountPages
{
    /// <summary>The name of the form that finds an account.</summary>
    public const string FindForm = "find-account";

    /// <summary>The name of the form that disables an account.</summary>
    public const string DisableForm = "disable-account";

    /// <summary>The name of the form that enables an account.</summary>
    public const string EnableForm = "enable-account";

    /// <summary>
    /// The title, in the browser, of the page that finds an account, its heading, and the text
    /// of the link that leads to it from the operator status screen.
    /// </summary>
    public static string Title => "Disable or enable an account";

    /// <summary>What the page is for, and how an account is found.</summary>
    public static string Explained =>
        "An operator disables the account of a user, and enables it again. Enter the email address of the account to find it.";

    /// <summary>What disabling an account does, and what it does not, said before any account is found.</summary>
    public static string DisabledMeans =>
        "A disabled account is signed out on every browser at once, and cannot sign in. Its agents that run move to " +
        "Attention required: they run no cycle and open no position, and an open position stays open with its stop-loss " +
        "order. Nothing of the account is deleted.";

    /// <summary>The text of the button that finds an account.</summary>
    public static string Find => "Find the account";

    /// <summary>What the page answers when no account has the address that was entered.</summary>
    public static string NoSuchAccount => "No account has this email address.";

    /// <summary>The title, in the browser, of the page of one account, and its heading.</summary>
    public static string AccountTitle => "Account of a user";

    /// <summary>
    /// Whether an account is disabled, and since when, with the address that names it: what the
    /// page of an account says over the button that switches it.
    /// </summary>
    public static string Stands(OperatorFoundAccount account, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(times);

        return account.DisabledAt is { } since
            ? $"The account of {account.Email} is disabled, since {times.At(since)}."
            : $"The account of {account.Email} is enabled.";
    }

    /// <summary>Why the page has no button for the account of an operator.</summary>
    public static string OperatorsAccount =>
        "It is the account of an operator, and the account of an operator is not disabled. Whoever is to be no operator is " +
        "taken off the list of the configuration file first.";

    /// <summary>What disabling the account does, before the button.</summary>
    public static string DisableMeans =>
        "Disabling it holds at once. Every browser of its user is signed out, and the agents of the account that run wait " +
        "for its user. It closes no position, and it deletes nothing.";

    /// <summary>What enabling the account does, before the button.</summary>
    public static string EnableMeans =>
        "Enabling it lets its user sign in again. Its agents stay as they are: its user resumes the ones that are to run.";

    /// <summary>The text of the button that disables the account.</summary>
    public static string Disable => "Disable the account";

    /// <summary>The text of the button that enables the account.</summary>
    public static string Enable => "Enable the account";

    /// <summary>That the page shows no more of a user than it takes to mean the right account (FR-OPS-5).</summary>
    public static string ShowsNothingElse =>
        "This page shows the address of the account and whether it is disabled, and nothing else of its user.";

    /// <summary>What is recorded, said under the button (FR-OPS-5).</summary>
    public static string Recorded =>
        "Disabling and enabling an account are each recorded in the audit trail, with who did it and when. Finding an " +
        "account changes nothing, and is not recorded.";

    /// <summary>The text of the link that leads from the page of an account back to the page that finds one.</summary>
    public static string FindAnother => "Find another account";
}
