namespace Ecnanib.Accounts;

/// <summary>
/// What an action in the audit trail was done to: a kind, and the id of the one that is meant
/// (NFR-LOG-7). See docs/accounts.md.
/// </summary>
/// <remarks>
/// A target is made by one of the methods here, from an id, and never from text. So what a
/// request brought cannot be written into the trail as a target. A component adds a method for
/// its kind of target when it is built.
/// </remarks>
public sealed class AuditTarget
{
    private AuditTarget(string text) => Text = text;

    /// <summary>The target as the trail stores it: the kind, a colon, and the id.</summary>
    internal string Text { get; }

    /// <summary>An account: <c>user:&lt;id&gt;</c>.</summary>
    public static AuditTarget Account(Guid userId) => new($"user:{userId}");

    /// <summary>
    /// A stored key of an AI provider, by the id of its row: <c>ai-key:&lt;id&gt;</c>. The id stays
    /// the same when the key is replaced, and says nothing of the key.
    /// </summary>
    public static AuditTarget AiKey(Guid credentialId) => new($"ai-key:{credentialId}");

    /// <summary>A stored key of Binance, by the id of its row: <c>exchange-key:&lt;id&gt;</c>.</summary>
    public static AuditTarget ExchangeKey(Guid credentialId) => new($"exchange-key:{credentialId}");
}
