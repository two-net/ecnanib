namespace Ecnanib.Modules.Catalog.Contracts;

/// <summary>
/// The model catalog (FR-CAT-01): every model any user's key has listed, merged into one list, and which
/// of a user's keys can use each one (FR-CAT-02). Metadata from the configuration file is applied as it's
/// read (FR-CAT-06). See Model catalog in docs/architecture.md.
/// </summary>
public interface ICatalog
{
    /// <summary>Every model, by provider and then ID, with the configuration's metadata applied.</summary>
    Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// What each of the user's AI keys can use, as of its last sync. Only the user's own keys (NFR-SEC-04).
    /// A key that was never synced has no rows.
    /// </summary>
    Task<IReadOnlyList<ModelAccess>> ListAccessAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>A model, as FR-CAT-01 describes it.</summary>
/// <param name="Provider">The AI provider's name, such as <c>OpenAI</c>.</param>
/// <param name="ModelId">The ID a request names it by.</param>
/// <param name="DisplayName">The configuration's name, else the provider's, else the ID.</param>
/// <param name="ReleasedAt">When the provider says it was released or created, if it says.</param>
/// <param name="Price">From the configuration, or null when it isn't known.</param>
/// <param name="FirstSeenAt">When a sync first listed it.</param>
/// <param name="LastSeenAt">When a sync last listed it.</param>
public sealed record CatalogModel(
    string Provider,
    string ModelId,
    string DisplayName,
    string? Family,
    DateTimeOffset? ReleasedAt,
    int? ContextWindow,
    int? MaxOutputTokens,
    ModelCapabilities Capabilities,
    ModelStatus Status,
    ModelPrice? Price,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>FR-CAT-01's capability flags. Null means neither the provider nor the configuration says.</summary>
/// <param name="ImageInput">It takes images in a prompt.</param>
/// <param name="StructuredOutput">It can be held to a JSON schema.</param>
/// <param name="ToolUse">It can call tools the request defines.</param>
/// <param name="LiveSearch">It can use the provider's own web or X search.</param>
/// <param name="ReasoningEffort">A request can set how hard it thinks.</param>
public sealed record ModelCapabilities(bool? ImageInput, bool? StructuredOutput, bool? ToolUse, bool? LiveSearch, bool? ReasoningEffort);

/// <summary>A model's price per million tokens, in US dollars, as exact decimals (DATA-R-02).</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    public const string Currency = "USD";
}

/// <summary>FR-CAT-01's statuses. Rows store the name, so a value is never renamed.</summary>
public enum ModelStatus
{
    Active,

    /// <summary>The provider stopped listing it (FR-CAT-04, #25).</summary>
    Deprecated,

    /// <summary>Missing for longer than the grace period (FR-CAT-04, #25).</summary>
    Retired,

    /// <summary>An administrator hid it (FR-CAT-05, #26).</summary>
    Hidden,
}

/// <summary>Whether one of the user's keys could use a model when it was last synced (FR-CAT-02).</summary>
/// <param name="Available">False once the key stopped listing it, or stopped being valid.</param>
/// <param name="CheckedAt">When the sync that wrote this began asking the provider.</param>
public sealed record ModelAccess(Guid CredentialId, string Provider, string ModelId, bool Available, DateTimeOffset CheckedAt);
