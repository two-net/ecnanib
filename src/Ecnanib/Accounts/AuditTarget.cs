namespace Ecnanib.Accounts;

/// <summary>
/// What an action in the audit trail was done to: a kind, and the id of the one that is meant
/// (NFR-LOG-7). See docs/accounts.md.
/// </summary>
/// <remarks>
/// A target is made by one of the methods here, from an id, and never from text. So what a
/// request brought cannot be written into the trail as a target. A component adds a method for
/// its kind of target when it is built. A target that there is one of in the whole service has
/// no id, and is a value here: <see cref="GlobalHalt"/> and <see cref="ModelCatalog"/>.
/// </remarks>
public sealed class AuditTarget
{
    private AuditTarget(string text) => Text = text;

    /// <summary>
    /// The global halt, which is a runtime flag of the service and belongs to no user:
    /// <c>runtime-flag:global-halt</c> (FR-OPS-3). It says nothing of who may switch it.
    /// </summary>
    public static AuditTarget GlobalHalt { get; } = new("runtime-flag:global-halt");

    /// <summary>
    /// The model catalog, which is of the service and belongs to no user: <c>catalog:models</c>
    /// (FR-CAT-2). An operator asks for a sync of it, of every provider that the service has a
    /// key for, so it names no provider.
    /// </summary>
    public static AuditTarget ModelCatalog { get; } = new("catalog:models");

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

    /// <summary>An agent, by the id of its row: <c>agent:&lt;id&gt;</c>. The id says nothing of what its user named it.</summary>
    public static AuditTarget Agent(Guid agentId) => new($"agent:{agentId}");

    /// <summary>
    /// Maintenance that an operator announced, by the id of its row: <c>maintenance:&lt;id&gt;</c>
    /// (NFR-REL-6). It is of the service and belongs to no user, and the id says nothing of when
    /// it is.
    /// </summary>
    public static AuditTarget Maintenance(Guid windowId) => new($"maintenance:{windowId}");
}
