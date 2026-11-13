using System.Globalization;
using Ecnanib.Accounts;
using Ecnanib.Credentials;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// What the pages of a user's keys have in common: how a stored key is shown, which is never as
/// the key (FR-KEY-8), what they say about a key that cannot be stored, and what they say about
/// what its provider said of it (FR-KEY-3 to FR-KEY-5).
/// </summary>
public static class KeyPages
{
    /// <summary>The name of the form that stores a key.</summary>
    public const string StoreForm = "store-key";

    /// <summary>The name of the form that deletes a key.</summary>
    public const string DeleteForm = "delete-key";

    /// <summary>
    /// The name of the form that checks the stored key of an AI provider again. The list has one
    /// for each stored key, and a page's forms each have a name of their own.
    /// </summary>
    public static string CheckAiForm(AiProviderCode provider) => $"check-ai-{provider.Value}";

    /// <summary>The name of the form that checks the stored Binance credential of an environment again.</summary>
    public static string CheckExchangeForm(TradingMode environment) => $"check-binance-{environment.ToString().ToLowerInvariant()}";

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

        return At(key.SavedAt);
    }

    /// <summary>What the last check of a stored key found, and when (FR-KEY-3).</summary>
    /// <param name="key">The stored key.</param>
    /// <param name="by">Who was asked about it, as users read it: the name of the provider, or "Binance".</param>
    public static string Checked(StoredKey key, string by)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key switch
        {
            { Status: KeyStatus.Valid, ValidatedAt: { } at } => $"Valid: {by} took it on {At(at)}.",
            { Status: KeyStatus.Invalid, ValidatedAt: { } at } =>
                $"Not valid: {by} refused it on {At(at)}. Nothing can use it until {by} takes it again.",
            _ => "Not checked yet. It is checked within the hour.",
        };
    }

    /// <summary>Which markets a Binance credential may trade on, as Binance last said (FR-KEY-5).</summary>
    public static string Trades(ExchangePermissions permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        return $"Spot trading: {Allowed(permissions.SpotTrading)}. Futures trading: {Allowed(permissions.FuturesTrading)}.";

        static string Allowed(bool permission) => permission ? "allowed" : "not allowed";
    }

    /// <summary>
    /// The fixed addresses that the service calls Binance from, for a user who restricts a key
    /// to listed addresses (FR-KEY-4). The configuration file may list none.
    /// </summary>
    public static IReadOnlyList<string> OutboundAddresses(BinanceSettings binance)
    {
        ArgumentNullException.ThrowIfNull(binance);

        return [.. binance.OutboundIps.Select(address => address.ToString())];
    }

    /// <summary>
    /// The warning about a Binance credential that works from any address (FR-KEY-4), with the
    /// addresses of the service when the configuration file lists them.
    /// </summary>
    public static string WorksFromAnyAddress(IReadOnlyList<string> outboundAddresses)
    {
        ArgumentNullException.ThrowIfNull(outboundAddresses);

        const string warning =
            "Binance reports that this key works from any IP address. A key that works only from listed addresses is safer.";

        return outboundAddresses.Count == 0
            ? warning
            : $"{warning} Restrict it at Binance to the addresses of this service, {string.Join(", ", outboundAddresses)}, and check it again.";
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

    /// <summary>
    /// What a page says when the key of an AI provider was not stored because of what the
    /// provider said about it, or null for an outcome that is not about that (FR-KEY-3). It
    /// never repeats what was entered.
    /// </summary>
    public static string? Says(StoreKeyOutcome outcome, string providerName) => outcome switch
    {
        StoreKeyOutcome.Refused =>
            $"{providerName} did not take this key, so nothing was stored. Copy the key again, whole, and see that it has not been revoked.",
        StoreKeyOutcome.Unreachable =>
            $"{providerName} gave no answer, so the key could not be checked and nothing was stored. Try again in a moment.",
        _ => null,
    };

    /// <summary>
    /// What a page says when a Binance credential was not stored because of what Binance said
    /// about it, or null for an outcome that is not about that (FR-KEY-3, FR-KEY-4).
    /// </summary>
    public static string? Says(StoreKeyOutcome outcome, TradingMode environment) => outcome switch
    {
        StoreKeyOutcome.CanWithdraw =>
            "Binance reports that this API key can withdraw. Ecnanib never takes a key that can move funds out of your account, " +
            "so nothing was stored. Turn off withdrawals for the key at Binance, or make a key without them, and enter the keys again.",
        StoreKeyOutcome.Refused =>
            $"Binance did not take these keys, so nothing was stored. See that both were copied whole and that they are keys of " +
            $"{Describe(environment)}. If the API key works only from listed IP addresses, it must list the addresses of this service.",
        StoreKeyOutcome.Unreachable =>
            "Binance gave no answer, so the keys could not be checked and nothing was stored. Try again in a moment.",
        _ => null,
    };

    /// <summary>What the list says after a stored key was checked again.</summary>
    /// <param name="outcome">How the check ended.</param>
    /// <param name="key">Which key, as users read it: "Claude key", or "Binance key for Demo".</param>
    public static string Says(KeyCheckOutcome outcome, string key) => outcome switch
    {
        KeyCheckOutcome.Valid => $"The {key} was checked again, and is valid.",
        KeyCheckOutcome.Invalid => $"The {key} was checked again, and was refused. Nothing can use it until it is taken again.",
        KeyCheckOutcome.Deleted =>
            $"Binance reports that the {key} can withdraw. Ecnanib never holds a key that can move funds out of your account, " +
            "so the key was deleted. Turn off withdrawals for it at Binance, or make a key without them, and add it again.",
        KeyCheckOutcome.Unreachable => $"The {key} could not be checked: no answer came. It is as it was. Try again in a moment.",
        KeyCheckOutcome.Unreadable => $"The {key} could not be checked, because the stored key cannot be read. Replace it.",
        KeyCheckOutcome.TooSoon => $"The {key} was checked less than a minute ago. Try again in a moment.",
        KeyCheckOutcome.Changed or KeyCheckOutcome.NoKey => $"The {key} changed while it was checked. What is stored now is shown below.",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "The outcome has no message."),
    };

    /// <summary>
    /// What a user is told when their Binance credential keeps an agent from starting. For a
    /// credential that lacks the permission of the agent's market, it names the permission
    /// (FR-KEY-5).
    /// </summary>
    public static string Says(StartBlock block, TradingMode environment) => block switch
    {
        StartBlock.NoKey => $"No Binance key is stored for {environment}. Add one, and start the agent again.",
        StartBlock.NotValid =>
            $"Your Binance key for {environment} is not valid: Binance did not take it when it was last checked. " +
            "Replace it, or check it again on the page of your keys.",
        StartBlock.SpotTradingMissing =>
            $"Your Binance key for {environment} lacks the Spot trading permission, which this agent's market needs. " +
            "Turn on Spot trading for the key at Binance, and check the key again on the page of your keys.",
        StartBlock.FuturesTradingMissing =>
            $"Your Binance key for {environment} lacks the Futures trading permission, which this agent's market needs. " +
            "Turn on Futures trading for the key at Binance, and check the key again on the page of your keys.",
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "The block has no message."),
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

    /// <summary>A time as a person reads it. The time is UTC.</summary>
    private static string At(DateTimeOffset time) =>
        time.UtcDateTime.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
