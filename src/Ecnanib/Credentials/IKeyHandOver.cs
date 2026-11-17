using Ecnanib.Shared;

namespace Ecnanib.Credentials;

/// <summary>
/// Hands a user's stored key to the call of an agent that needs it: decrypted only in memory,
/// and only for that call (FR-KEY-9). See "Handing a key to a call" in docs/credentials.md.
/// </summary>
/// <remarks>
/// <para>
/// What calls a model for an agent asks for the key of the agent's user as it makes the call,
/// hands it to the AI Provider Gateway with that call, and keeps it nowhere: not in a field, not
/// in a record, and not in a log event. It asks again for the next call.
/// </para>
/// <para>
/// It takes the user by id, and not from a request: a decision cycle works for an agent, where
/// nobody is signed in. The id is that of the agent's row, and never something that came in from
/// outside, such as the output of a model. See docs/ownership.md.
/// </para>
/// <para>
/// A user's own key is the only key that a call of their agent is made with (FR-KEY-1): there
/// is no key of the service to fall back on.
/// </para>
/// </remarks>
public interface IKeyHandOver
{
    /// <summary>The key of a user for an AI provider, for one call of a model.</summary>
    /// <param name="userId">The user whose agent the call is for.</param>
    /// <param name="provider">The provider of the model that is called.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<HandedKey> AiKeyAsync(Guid userId, AiProviderCode provider, CancellationToken cancellationToken);
}

/// <summary>What came of asking for a key.</summary>
public enum KeyHandOverOutcome
{
    /// <summary>The key is handed over.</summary>
    Handed,

    /// <summary>The user has stored no key for the provider.</summary>
    NoKey,

    /// <summary>The provider refused the key when it was last asked about it. No call is made with such a key.</summary>
    Invalid,

    /// <summary>The stored value does not decrypt: it names a master key that the configuration file does not list.</summary>
    Unreadable,
}

/// <summary>A key that was asked for: the key itself, or why there is none.</summary>
/// <remarks>
/// It is a class of its own, and no record, so that nothing writes the key by writing this: its
/// text is the outcome alone.
/// </remarks>
public sealed class HandedKey
{
    private HandedKey(KeyHandOverOutcome outcome, string? key)
    {
        Outcome = outcome;
        Key = key;
    }

    /// <summary>What came of asking.</summary>
    public KeyHandOverOutcome Outcome { get; }

    /// <summary>The key as text, when it is handed over, and null otherwise.</summary>
    public string? Key { get; }

    /// <summary>A key that is handed over.</summary>
    public static HandedKey Of(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return new HandedKey(KeyHandOverOutcome.Handed, key);
    }

    /// <summary>No key, and why.</summary>
    public static HandedKey None(KeyHandOverOutcome why) => why == KeyHandOverOutcome.Handed
        ? throw new ArgumentOutOfRangeException(nameof(why), why, "A key that is handed over is a key.")
        : new HandedKey(why, key: null);

    /// <summary>The outcome, and never the key.</summary>
    public override string ToString() => Outcome.ToString();
}
