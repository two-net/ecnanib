using Ecnanib.Platform.Persistence;
using Ecnanib.Shared;

namespace Ecnanib.Credentials;

/// <summary>
/// The keys a user brings: one for each AI provider, and one Binance credential for each
/// environment (FR-KEY-1, FR-KEY-2). A key is stored encrypted and is never handed back: after
/// saving, only its last four characters are shown (FR-KEY-8). A user can replace or delete any
/// key at any time (FR-KEY-10). See docs/credentials.md.
/// </summary>
/// <remarks>
/// Each method is for the user who is signed in on the request it is given, and fails for a
/// request that is not signed in. Storing a key is a sensitive action: it is done only for a user
/// who has just proved again who they are (FR-ACC-4), and whose email address is verified.
/// Deleting one is not.
/// </remarks>
public interface IStoredKeys
{
    /// <summary>
    /// What the user has stored, for the pages that show it: every AI provider and both
    /// environments of Binance, each with the last four characters of its key or with none.
    /// </summary>
    Task<StoredKeysState> StateAsync(HttpContext request);

    /// <summary>
    /// Stores the user's key for an AI provider: as a new one, or in place of the one that is
    /// stored, which is gone from then on.
    /// </summary>
    /// <param name="request">The request that posted the form.</param>
    /// <param name="provider">The provider the key is for.</param>
    /// <param name="apiKey">The key, as it was typed. Spaces around it are left out.</param>
    Task<StoreKeyResult> StoreAiKeyAsync(HttpContext request, AiProviderCode provider, string apiKey);

    /// <summary>
    /// Stores the user's Binance credential for an environment: as a new one, or in place of the
    /// one that is stored, which is gone from then on.
    /// </summary>
    /// <param name="request">The request that posted the form.</param>
    /// <param name="environment">Binance's test environment or the real exchange.</param>
    /// <param name="apiKey">The API key, as it was typed. Spaces around it are left out.</param>
    /// <param name="secretKey">The secret key that belongs to it, likewise.</param>
    Task<StoreKeyResult> StoreExchangeKeyAsync(HttpContext request, TradingMode environment, string apiKey, string secretKey);

    /// <summary>
    /// Deletes the user's key for an AI provider, for good. What depends on the key is told
    /// first: see <see cref="IKeyDependents"/>. Nothing happens when none is stored, so a form
    /// that is sent twice is answered as the first one was.
    /// </summary>
    Task DeleteAiKeyAsync(HttpContext request, AiProviderCode provider);

    /// <summary>Deletes the user's Binance credential for an environment, for good, in the same way.</summary>
    Task DeleteExchangeKeyAsync(HttpContext request, TradingMode environment);
}

/// <summary>The rules a key is held to before it is stored. They say nothing about whether a provider accepts it.</summary>
public static class KeyRules
{
    /// <summary>
    /// The fewest characters a key has. A key is shown by its last four, which must not be most
    /// of it.
    /// </summary>
    public const int MinimumLength = 16;

    /// <summary>The most characters a key has. No provider's key comes near it.</summary>
    public const int MaximumLength = 512;

    /// <summary>How many characters of a stored key are shown: its last ones.</summary>
    public const int ShownCharacters = 4;

    /// <summary>What is wrong with a key, or null when it can be stored. The key is taken as it is: trim it first.</summary>
    public static KeyProblem? ProblemOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key switch
        {
            { Length: 0 } => KeyProblem.Missing,
            { Length: < MinimumLength } => KeyProblem.TooShort,
            { Length: > MaximumLength } => KeyProblem.TooLong,

            // A key is letters, digits, and signs of the ASCII set, in one piece. Anything else
            // was not copied whole, and could not be sent in the header of a call.
            _ when key.Any(character => character is <= ' ' or > '~') => KeyProblem.NotInOnePiece,
            _ => null,
        };
    }

    /// <summary>The characters of a key that are shown in its place.</summary>
    public static string LastCharactersOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return key[^ShownCharacters..];
    }
}

/// <summary>What keeps a key from being stored.</summary>
public enum KeyProblem
{
    /// <summary>Nothing was entered.</summary>
    Missing,

    /// <summary>It has fewer characters than <see cref="KeyRules.MinimumLength"/>.</summary>
    TooShort,

    /// <summary>It has more characters than <see cref="KeyRules.MaximumLength"/>.</summary>
    TooLong,

    /// <summary>It holds a space, a line break, or a character that no key has.</summary>
    NotInOnePiece,
}

/// <summary>What a user has stored of keys.</summary>
/// <param name="AddressVerified">
/// Whether the email address of the account is verified. A key is stored only for an account
/// whose address is: see <see cref="StoreKeyOutcome.AddressNotVerified"/>.
/// </param>
/// <param name="AiKeys">Every AI provider, by its code, with the user's key for it when there is one.</param>
/// <param name="ExchangeKeys">Both environments of Binance, Demo first, with the user's credential when there is one.</param>
public sealed record StoredKeysState(
    bool AddressVerified, IReadOnlyList<AiKeySlot> AiKeys, IReadOnlyList<ExchangeKeySlot> ExchangeKeys);

/// <summary>The place of a user's key for one AI provider.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="ProviderName">Its name as users read it, such as "Claude".</param>
/// <param name="Key">What is stored, or null when nothing is.</param>
public sealed record AiKeySlot(AiProviderCode Provider, string ProviderName, StoredKey? Key);

/// <summary>The place of a user's Binance credential for one environment.</summary>
/// <param name="Environment">The environment.</param>
/// <param name="Key">What is stored, or null when nothing is.</param>
public sealed record ExchangeKeySlot(TradingMode Environment, StoredKey? Key);

/// <summary>A stored key, as it is shown: never the key.</summary>
/// <param name="LastCharacters">Its last four characters. For a Binance credential, those of the API key.</param>
/// <param name="SavedAt">When it was stored.</param>
public sealed record StoredKey(string LastCharacters, DateTimeOffset SavedAt);

/// <summary>How storing a key ended.</summary>
public enum StoreKeyOutcome
{
    /// <summary>The key is stored, and the one before it is gone.</summary>
    Stored,

    /// <summary>
    /// The user has not proved again who they are on this browser, or did so too long ago.
    /// Nothing was stored.
    /// </summary>
    ConfirmationNeeded,

    /// <summary>
    /// The email address of the account is not verified. Such an account may have been registered
    /// by someone the address does not belong to, and its owner can still take it over, with
    /// whatever key is stored in it. Nothing was stored.
    /// </summary>
    AddressNotVerified,

    /// <summary>What was entered cannot be a key. Nothing was stored.</summary>
    NotAKey,

    /// <summary>There is no such AI provider. Nothing was stored.</summary>
    NoSuchProvider,
}

/// <summary>How storing a key ended, with what was wrong with what was entered.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ApiKey">What is wrong with the key, or with the API key of a Binance credential.</param>
/// <param name="SecretKey">What is wrong with the secret key of a Binance credential.</param>
public sealed record StoreKeyResult(StoreKeyOutcome Outcome, KeyProblem? ApiKey = null, KeyProblem? SecretKey = null);

/// <summary>
/// What Credentials tells whatever depends on a key, when the key goes: an agent that depends on
/// a missing key moves to Attention required (FR-KEY-10). Credentials may not use Agents, so it
/// declares this interface, and Agents implements and registers it. See "When a lower component
/// must trigger a higher one" in docs/architecture.md.
/// </summary>
/// <remarks>
/// <para>
/// Nothing implements it until agents are built. Until then nothing depends on a key, and a key
/// is deleted without anything being told.
/// </para>
/// <para>
/// It is told inside the transaction that deletes the key, before the row goes, with the context
/// that holds the transaction. So what an implementation changes is stored when the key is
/// deleted, and not when it is not. An implementation asks for the rows of the user it is given,
/// and commits nothing.
/// </para>
/// </remarks>
public interface IKeyDependents
{
    /// <summary>A user's key for an AI provider is being deleted.</summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user whose key it is.</param>
    /// <param name="provider">The provider the key is for.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    Task AiKeyDeletedAsync(EcnanibDbContext context, Guid userId, AiProviderCode provider, CancellationToken cancellationToken);

    /// <summary>A user's Binance credential for an environment is being deleted.</summary>
    /// <param name="context">The context that holds the transaction.</param>
    /// <param name="userId">The user whose credential it is.</param>
    /// <param name="environment">The environment the credential is for.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    Task ExchangeKeyDeletedAsync(EcnanibDbContext context, Guid userId, TradingMode environment, CancellationToken cancellationToken);
}
