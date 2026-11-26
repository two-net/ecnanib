using Ecnanib.Accounts;
using Ecnanib.Credentials;
using Ecnanib.DecisionEngine;
using Ecnanib.Platform.Settings;
using Ecnanib.Shared;
using Ecnanib.WebUi.Pages.Agents;
using Ecnanib.WebUi.Pages.Usage;

namespace Ecnanib.WebUi.Pages.Account;

/// <summary>
/// The texts of the pages of a user's keys, in one place (NFR-USA-4): the list of the keys, the
/// pages that add, replace, and delete the key of an AI provider, and those that do so with the
/// keys of Binance. And what those pages have in common: how a stored key is shown, which is
/// never as the key (FR-KEY-8), what they say about a key that cannot be stored, and what they
/// say about what its provider said of it (FR-KEY-3 to FR-KEY-5). They say the usage of each key
/// for an AI provider as well: calls, tokens, and estimated cost (FR-KEY-11). See
/// docs/credentials.md.
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
    public static string VerifyFirst =>
        "A key can be stored once your email address is verified. Open the link in the email you were sent when you registered.";

    /// <summary>
    /// What an AI provider is sent, and what it never is, as every page says it (NFR-SEC-8). The
    /// four kinds of data are the ones that a prompt is composed from: see docs/prompts.md.
    /// </summary>
    private const string WhatIsSent =
        "What is sent is limited to market data, the settings of the agent, the state of its position, and lessons from " +
        "its earlier trades. It never holds your email address, your password, your keys, or anything else that says who you are.";

    /// <summary>
    /// What the pages say about the data that goes to AI providers: to the providers that the
    /// user configures, under the user's own keys and those providers' terms (NFR-SEC-8).
    /// </summary>
    public static string SentToProviders =>
        "When an agent works, each of its roles sends its prompt to the AI provider of the model you gave it, under your " +
        "own key for that provider. " + WhatIsSent + " Each provider handles what it is sent under its own terms, which " +
        "apply to your account with it.";

    /// <summary>The same, on the page that takes the key of one provider, before the key is saved.</summary>
    /// <param name="providerName">The name of the provider, as users read it.</param>
    public static string SentTo(string providerName) =>
        $"When an agent works, each role that you gave a model of {providerName} sends its prompt to {providerName}, under " +
        $"this key. {WhatIsSent} {providerName} handles what it is sent under its own terms, which apply to your account there.";

    /// <summary>A stored key as it is shown: dots, and its last four characters.</summary>
    public static string Masked(StoredKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return $"••••{key.LastCharacters}";
    }

    /// <summary>When a key was stored, as a person reads it, in the time zone of the page.</summary>
    public static string Saved(StoredKey key, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(times);

        return times.At(key.SavedAt);
    }

    /// <summary>What the last check of a stored key found, and when (FR-KEY-3).</summary>
    /// <param name="key">The stored key.</param>
    /// <param name="by">Who was asked about it, as users read it: the name of the provider, or "Binance".</param>
    /// <param name="times">The times of the page.</param>
    public static string Checked(StoredKey key, string by, PageTimes times)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(times);

        return key switch
        {
            { Status: KeyStatus.Valid, ValidatedAt: { } at } => $"Valid: {by} took it on {times.At(at)}.",
            { Status: KeyStatus.Invalid, ValidatedAt: { } at } =>
                $"Not valid: {by} refused it on {times.At(at)}. Nothing can use it until {by} takes it again.",
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
        StartBlock.NoKey => $"No Binance key is stored for {AgentPages.NameOf(environment)}. Add one, and start the agent again.",
        StartBlock.NotValid =>
            $"Your Binance key for {AgentPages.NameOf(environment)} is not valid: Binance did not take it when it was last " +
            "checked. Replace it, or check it again on the page of your keys.",
        StartBlock.SpotTradingMissing =>
            $"Your Binance key for {AgentPages.NameOf(environment)} lacks the Spot trading permission, which this agent's " +
            "market needs. Turn on Spot trading for the key at Binance, and check the key again on the page of your keys.",
        StartBlock.FuturesTradingMissing =>
            $"Your Binance key for {AgentPages.NameOf(environment)} lacks the Futures trading permission, which this agent's " +
            "market needs. Turn on Futures trading for the key at Binance, and check the key again on the page of your keys.",
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

    /// <summary>What the usage of the keys is, and what its cost is not (FR-KEY-11).</summary>
    public static string UsageExplained =>
        "The usage of a key is the calls that the roles of your agents made with it: how many, the tokens that the provider " +
        "counted for them, and what those are estimated to cost. It is an estimate, and \"unknown\" where a model has no " +
        "price here. What you are charged is on your account with the provider.";

    /// <summary>
    /// The usage of a stored key for an AI provider (FR-KEY-11): the calls to its provider in
    /// the last days, or since the key was saved when that is later, because a call before
    /// then was made with the key that this one took the place of.
    /// </summary>
    public static string Usage(KeyUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var since = usage.SinceSaved ? "Usage since this key was saved" : $"Usage in the last {UsagePages.Whole(KeyUsage.Days)} days";

        return $"{since}: {UsagePages.Sentence(usage.Figures)}";
    }

    /// <summary>The title of the list of the keys, in the browser, and its heading.</summary>
    public static string Title => "Your API keys";

    /// <summary>Whose keys the agents work with, and how many there are.</summary>
    public static string Explained =>
        "Your agents work with your own keys: one for each AI provider, and one for Binance in each environment. Ecnanib " +
        "has no key of its own for a decision or an order.";

    /// <summary>How a key is checked and kept, and what a user can do with it.</summary>
    public static string Kept =>
        "A key is checked with its provider before it is stored, and again every day. It is stored encrypted and is never " +
        "shown again. After saving, you see only its last four characters. You can replace or delete a key at any time.";

    /// <summary>The heading of the keys of the AI providers in the list.</summary>
    public static string AiProvidersHeading => "AI providers";

    /// <summary>What stands before a stored key of an AI provider in the list, which is shown by its last four characters.</summary>
    public static string Key => "Key";

    /// <summary>
    /// The API key of Binance: what stands before a stored one in the list, which is shown by its
    /// last four characters, and the label of the field that takes a key, on the page of either
    /// kind of key.
    /// </summary>
    public static string ApiKey => "API key";

    /// <summary>When a stored key was saved, as the list says it after the key and a comma.</summary>
    public static string WasSaved(StoredKey key, PageTimes times) => $"saved {Saved(key, times)}.";

    /// <summary>What the last check of a stored Binance credential found, and when (FR-KEY-3).</summary>
    public static string CheckedByBinance(StoredKey key, PageTimes times) => Checked(key, "Binance", times);

    /// <summary>The text of the link to the page that replaces a stored key.</summary>
    /// <param name="name">Which key, as users read it: the name of the provider, or of the environment of Binance.</param>
    public static string ReplaceLink(string name) => $"Replace the {name} key";

    /// <summary>The text of the link to the page that deletes a stored key.</summary>
    /// <param name="name">Which key, as users read it: the name of the provider, or of the environment of Binance.</param>
    public static string DeleteLink(string name) => $"Delete the {name} key";

    /// <summary>The button that asks the provider about a stored key again.</summary>
    /// <param name="name">Which key, as users read it: the name of the provider, or of the environment of Binance.</param>
    public static string CheckAgain(string name) => $"Check the {name} key again";

    /// <summary>What the list says where no key is stored.</summary>
    public static string NoKey => "No key stored.";

    /// <summary>The text of the link to the page that adds a key where none is stored.</summary>
    /// <param name="name">Which key, as users read it: the name of the provider, or of the environment of Binance.</param>
    public static string AddLink(string name) => $"Add a {name} key";

    /// <summary>The heading of the keys of Binance in the list.</summary>
    public static string BinanceHeading => "Binance";

    /// <summary>What Ecnanib never does with the funds at Binance, over the keys of Binance in the list.</summary>
    public static string NeverWithdraws => "Ecnanib never withdraws, deposits, or transfers funds, and takes no key that can withdraw.";

    /// <summary>Which key of an AI provider was checked again, as what the list says then names it: "Claude key".</summary>
    public static string AiKeyName(string providerName) => $"{providerName} key";

    /// <summary>Which Binance credential was checked again, as what the list says then names it: "Binance key for Demo".</summary>
    public static string ExchangeKeyName(TradingMode environment) => $"Binance key for {AgentPages.NameOf(environment)}";

    /// <summary>
    /// The title of the page that takes the key of an AI provider, in the browser, and its
    /// heading: it adds a key where none is stored, and replaces the stored one otherwise.
    /// </summary>
    public static string StoreTitle(AiKeySlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        return slot.Key is null ? $"Add your {slot.ProviderName} key" : $"Replace your {slot.ProviderName} key";
    }

    /// <summary>What that page says of the stored key before it shows it, when one is stored.</summary>
    public static string AiKeyStoredNow => "The key that is stored now is";

    /// <summary>What becomes of the stored key, after it.</summary>
    public static string AiKeyReplaced => "The new key takes its place, and the stored one is gone for good.";

    /// <summary>What the key of an AI provider is used for, and how it is checked and kept.</summary>
    /// <param name="providerName">The name of the provider, as users read it.</param>
    public static string AiKeyExplained(string providerName) =>
        $"Calls to {providerName} for your agents are made with this key, and are charged to your own account there. When you " +
        $"save it, {providerName} is asked whether it takes the key, with a call that only reads, and the key is stored only " +
        "when it does. The key is stored encrypted. Once it is saved, it is never shown again: you see only its last four characters.";

    /// <summary>The hint under the field that takes the key of an AI provider.</summary>
    /// <param name="providerName">The name of the provider, as users read it.</param>
    public static string PasteAsGiven(string providerName) => $"Paste the key as {providerName} gave it to you.";

    /// <summary>The button that saves the key of an AI provider.</summary>
    public static string SaveKey => "Save the key";

    /// <summary>The text of the link that leads from a page of one key back to the list.</summary>
    public static string BackToKeys => "Back to your keys";

    /// <summary>The title of the page that deletes the key of an AI provider, in the browser, and its heading.</summary>
    /// <param name="providerName">The name of the provider, as users read it.</param>
    public static string DeleteTitle(string providerName) => $"Delete your {providerName} key";

    /// <summary>What that page deletes, before the stored key.</summary>
    public static string DeletesAiKey => "This deletes the key";

    /// <summary>That deleting it is for good, after the stored key and a comma.</summary>
    /// <param name="providerName">The name of the provider, as users read it.</param>
    public static string AiKeyGone(string providerName) =>
        $"at once and for good. It cannot be undone: to use {providerName} again, you add a key anew.";

    /// <summary>What becomes of the agents that use the key of that provider.</summary>
    /// <param name="providerName">The name of the provider, as users read it.</param>
    public static string AiAgentsStop(string providerName) =>
        $"An agent that works with {providerName} cannot run without this key. It stops and asks for your attention.";

    /// <summary>The button that deletes the key of an AI provider.</summary>
    public static string DeleteKey => "Delete the key";

    /// <summary>The text of the link that leads back to the list, and deletes nothing.</summary>
    public static string KeepKey => "Keep the key";

    /// <summary>
    /// The title of the page that takes the keys of Binance for an environment, in the browser,
    /// and its heading: it adds keys where none are stored, and replaces the stored ones otherwise.
    /// </summary>
    public static string StoreTitle(ExchangeKeySlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        return slot.Key is null
            ? $"Add your Binance keys for {AgentPages.NameOf(slot.Environment)}"
            : $"Replace your Binance keys for {AgentPages.NameOf(slot.Environment)}";
    }

    /// <summary>What that page says of the stored API key before it shows it, when one is stored.</summary>
    public static string ExchangeKeyStoredNow => "The API key that is stored now is";

    /// <summary>What becomes of the stored keys, after it.</summary>
    public static string ExchangeKeysReplaced =>
        "The new keys take the place of it and of its secret key, and the stored ones are gone for good.";

    /// <summary>What the keys of an environment of Binance are used for, and how they are kept.</summary>
    public static string ExchangeKeysExplained(TradingMode environment) =>
        $"This is for {Describe(environment)}. Orders of your agents in {AgentPages.NameOf(environment)} are placed with these " +
        "keys. Both are stored encrypted. Once they are saved, neither is shown again: you see only the last four characters " +
        "of the API key.";

    /// <summary>How Binance is asked about the keys, before the warning about withdrawals.</summary>
    public static string AskedWhatTheyMayDo => "When you save the keys, Binance is asked what they may do, with calls that only read.";

    /// <summary>The warning about withdrawals, in bold.</summary>
    public static string WithoutWithdrawals => "Make the API key without the permission to withdraw.";

    /// <summary>Why, and which permissions the key needs, after that.</summary>
    public static string PermissionsNeeded =>
        "Ecnanib never withdraws, deposits, or transfers funds, and stores no key that can withdraw. The key needs the " +
        "permission to trade on the market of each agent that uses it: Spot trading, Futures trading, or both.";

    /// <summary>What the page says over the addresses that the service calls Binance from (FR-KEY-4).</summary>
    public static string ListedAddresses =>
        "A key that works only from listed IP addresses is safer. If you restrict the key at Binance, list the addresses " +
        "that Ecnanib calls Binance from:";

    /// <summary>The label of the field that takes the secret key of Binance.</summary>
    public static string SecretKeyLabel => "Secret key";

    /// <summary>The hint under that field.</summary>
    public static string SecretKeyHint => "Binance shows the secret key once, when the API key is created.";

    /// <summary>The button that saves the keys of Binance.</summary>
    public static string SaveKeys => "Save the keys";

    /// <summary>The title of the page that deletes the keys of Binance for an environment, in the browser, and its heading.</summary>
    public static string DeleteTitle(TradingMode environment) => $"Delete your Binance keys for {AgentPages.NameOf(environment)}";

    /// <summary>What that page deletes, before the stored API key.</summary>
    public static string DeletesExchangeKey => "This deletes the API key";

    /// <summary>That deleting it is for good, after the stored API key.</summary>
    public static string ExchangeKeysGone(TradingMode environment) =>
        $"and its secret key, at once and for good. It cannot be undone: to trade in {AgentPages.NameOf(environment)} again, " +
        "you add the keys anew.";

    /// <summary>What becomes of the agents that trade in that environment, and of their positions.</summary>
    public static string ExchangeAgentsStop(TradingMode environment) =>
        $"An agent that trades in {AgentPages.NameOf(environment)} cannot run without these keys. It stops and asks for your " +
        "attention. A position that is open on Binance stays as it is there, with its protective orders.";

    /// <summary>The button that deletes the keys of Binance.</summary>
    public static string DeleteKeys => "Delete the keys";

    /// <summary>The text of the link that leads back to the list, and deletes nothing.</summary>
    public static string KeepKeys => "Keep the keys";
}
