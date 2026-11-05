using Ecnanib.Kernel;

namespace Ecnanib.Modules.Catalog.Contracts;

/// <summary>
/// The model catalog (FR-CAT-01): every model any user's key has listed, merged into one list, and which
/// of a user's keys can use each one (FR-CAT-02). Metadata from the configuration file is applied as it's
/// read (FR-CAT-06). See Model catalog in docs/architecture.md.
/// </summary>
public interface ICatalog
{
    /// <summary>
    /// Every model, by provider and then ID, with the configuration's metadata and then administrators'
    /// overrides applied (FR-CAT-05). Hidden ones are included, flagged <see cref="CatalogModel.Hidden"/>:
    /// whoever offers models to users leaves them out.
    /// </summary>
    Task<IReadOnlyList<CatalogModel>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The AI providers that have an adapter, by name, so the Web UI can list them without AiAdapters.</summary>
    IReadOnlyList<string> Providers { get; }

    /// <summary>
    /// What each of the user's AI keys can use, as of its last sync. Only the user's own keys (NFR-SEC-04).
    /// A key that was never synced has no rows.
    /// </summary>
    Task<IReadOnlyList<ModelAccess>> ListAccessAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// The default provider, and optionally model, for each role that has one (FR-ROLE-03), in role order.
    /// Seeded from the RoleDefaults section (NFR-DEP-02), and edited by administrators after that, who can also
    /// leave a role with no default.
    /// </summary>
    Task<IReadOnlyList<RoleDefault>> ListRoleDefaultsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A model, as FR-CAT-01 describes it. Each field an administrator can override comes from the override,
/// else the configuration, else the provider.
/// </summary>
/// <param name="Provider">The AI provider's name, such as <c>OpenAI</c>.</param>
/// <param name="ModelId">The ID a request names it by.</param>
/// <param name="DisplayName">An administrator's name, else the configuration's, else the provider's, else the ID.</param>
/// <param name="ReleasedAt">When the provider says it was released or created, if it says.</param>
/// <param name="Status">
/// Where it is in its lifecycle (FR-CAT-04): Active, Deprecated or Retired, never Hidden. Hiding it doesn't
/// change this, so a hidden model that providers drop is still deprecated and retired.
/// </param>
/// <param name="Price">From an administrator or the configuration, or null when it isn't known.</param>
/// <param name="FirstSeenAt">When a sync first listed it.</param>
/// <param name="LastSeenAt">When a sync last listed it.</param>
/// <param name="Hidden">
/// An administrator withdrew it from offer (FR-CAT-05): it's left off the catalog page, role options and
/// defaults, while agents that already use it keep it.
/// </param>
/// <param name="RecommendedRoles">
/// The roles an administrator recommends it for, in role order. A recommended model comes before its provider's
/// others in that role's options and defaults.
/// </param>
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
    DateTimeOffset LastSeenAt,
    bool Hidden,
    IReadOnlyList<AiRole> RecommendedRoles)
{
    /// <summary>Hidden for an Active model an administrator hid, else its lifecycle status. What the pages show.</summary>
    public ModelStatus ShownStatus => Hidden && Status == ModelStatus.Active ? ModelStatus.Hidden : Status;

    public bool IsRecommendedFor(AiRole role) => RecommendedRoles.Contains(role);
}

/// <summary>FR-CAT-01's capability flags. Null means neither the provider nor the configuration says.</summary>
/// <param name="ImageInput">It takes images in a prompt.</param>
/// <param name="StructuredOutput">It can be held to a JSON schema.</param>
/// <param name="ToolUse">It can call tools the request defines.</param>
/// <param name="LiveSearch">It can use the provider's own web or X search.</param>
/// <param name="ReasoningEffort">A request can set how hard it thinks.</param>
public sealed record ModelCapabilities(bool? ImageInput, bool? StructuredOutput, bool? ToolUse, bool? LiveSearch, bool? ReasoningEffort)
{
    public static readonly ModelCapabilities Unknown = new(null, null, null, null, null);

    /// <summary>What the model says about one capability, or null when nobody said.</summary>
    public bool? Of(ModelCapability capability) => capability switch
    {
        ModelCapability.ImageInput => ImageInput,
        ModelCapability.StructuredOutput => StructuredOutput,
        ModelCapability.ToolUse => ToolUse,
        ModelCapability.LiveSearch => LiveSearch,
        ModelCapability.ReasoningEffort => ReasoningEffort,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
    };

    /// <summary>Each field from <paramref name="above"/> where it's set, else from this.</summary>
    public ModelCapabilities Under(ModelCapabilities above) => new(
        above.ImageInput ?? ImageInput,
        above.StructuredOutput ?? StructuredOutput,
        above.ToolUse ?? ToolUse,
        above.LiveSearch ?? LiveSearch,
        above.ReasoningEffort ?? ReasoningEffort);
}

/// <summary>FR-CAT-01's capability flags, one at a time, as the catalog page filters by them (FR-CAT-07).</summary>
public enum ModelCapability
{
    ImageInput,
    StructuredOutput,
    ToolUse,
    LiveSearch,
    ReasoningEffort,
}

/// <summary>A model's price per million tokens, in US dollars, as exact decimals (DATA-R-02).</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion)
{
    public const string Currency = "USD";
}

/// <summary>
/// FR-CAT-01's statuses. Rows store the name, so a value is never renamed. A model's row holds only the first
/// three: see <see cref="Hidden"/>.
/// </summary>
public enum ModelStatus
{
    Active,

    /// <summary>A sync of everyone's keys found that none lists it, or can use it, any more (FR-CAT-04).</summary>
    Deprecated,

    /// <summary>Missing for longer than Catalog:RetireAfterMissingDays (FR-CAT-04). Listed again, it's Active.</summary>
    Retired,

    /// <summary>
    /// What the pages show for an Active model an administrator hid (FR-CAT-05). Never stored: hiding is an
    /// override, and the model keeps its lifecycle status under it. See <see cref="CatalogModel.ShownStatus"/>.
    /// </summary>
    Hidden,
}

/// <summary>Whether one of the user's keys could use a model when it was last synced (FR-CAT-02).</summary>
/// <param name="Available">False once the key stopped listing it, or stopped being valid.</param>
/// <param name="CheckedAt">When the sync that wrote this began asking the provider.</param>
public sealed record ModelAccess(Guid CredentialId, string Provider, string ModelId, bool Available, DateTimeOffset CheckedAt);

/// <summary>A role's default (FR-ROLE-03): a provider by strength, and optionally the model to use from it.</summary>
/// <param name="Provider">The AI provider's name, such as <c>OpenAI</c>.</param>
/// <param name="ModelId">The model to use, or null for the provider's best one that the user can use.</param>
public sealed record RoleDefault(AiRole Role, string Provider, string? ModelId);
