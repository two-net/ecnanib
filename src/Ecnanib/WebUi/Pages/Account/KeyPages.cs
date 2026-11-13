using System.Globalization;
using Ecnanib.Accounts;
using Ecnanib.Credentials;
using Ecnanib.Shared;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// What the pages of a user's keys have in common: how a stored key is shown, which is never as
/// the key (FR-KEY-8), and what they say about a key that cannot be stored.
/// </summary>
public static class KeyPages
{
    /// <summary>The name of the form that stores a key.</summary>
    public const string StoreForm = "store-key";

    /// <summary>The name of the form that deletes a key.</summary>
    public const string DeleteForm = "delete-key";

    /// <summary>
    /// Where a page that stores a key sends a user who has not just proved who they are: to the
    /// page that takes the proof, and from there back.
    /// </summary>
    public static string ConfirmFirst(string page) =>
        SessionAuthentication.PageWith(SessionAuthentication.ReauthenticationPath, page);

    /// <summary>
    /// What a page says to a user whose email address is not verified, in place of the form that
    /// takes a key: such an account may not be its user's to keep.
    /// </summary>
    public const string VerifyFirst =
        "A key can be stored once your email address is verified. Open the link in the email you were sent when you registered.";

    /// <summary>A stored key as it is shown: dots, and its last four characters.</summary>
    public static string Masked(StoredKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return $"••••{key.LastCharacters}";
    }

    /// <summary>When a key was stored, as a person reads it. The time is UTC.</summary>
    public static string Saved(StoredKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key.SavedAt.UtcDateTime.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    /// <summary>The environment that a path names, or null when it names none: <c>demo</c> or <c>live</c>.</summary>
    public static TradingMode? EnvironmentOf(string? text) =>
        Enum.GetValues<TradingMode>()
            .Cast<TradingMode?>()
            .SingleOrDefault(environment => string.Equals(environment.ToString(), text, StringComparison.OrdinalIgnoreCase));

    /// <summary>What an environment of Binance is, in a few words.</summary>
    public static string Describe(TradingMode environment) => environment switch
    {
        TradingMode.Demo => "Binance's test environment, with no real funds",
        TradingMode.Live => "your real Binance account",
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "The environment has no description."),
    };

    /// <summary>What is wrong with what was entered as a key. It never repeats what was entered.</summary>
    public static string Says(KeyProblem problem) => problem switch
    {
        KeyProblem.Missing => "Enter the key.",
        KeyProblem.TooShort => $"That is too short to be a key. A key has {KeyRules.MinimumLength} characters or more.",
        KeyProblem.TooLong => $"That is too long to be a key. A key has {KeyRules.MaximumLength} characters at most.",
        KeyProblem.NotInOnePiece => "A key is in one piece: letters, digits, and signs, with no space or line break in it. Copy it again, whole.",
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "The problem has no message."),
    };
}
