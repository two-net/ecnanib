namespace Ecnanib.Modules.AiAdapters.Contracts;

/// <summary>
/// A model that a key can use, as its provider lists it (EXT-AI-02). Providers expose different details,
/// so everything but the ID is null where a provider doesn't give it. The catalog fills the gaps from the
/// configuration and administrators (FR-CAT-06).
/// </summary>
/// <param name="Id">The ID a request names it by, such as "claude-opus-4-1".</param>
public sealed record AiModel(
    string Id,
    string? DisplayName = null,
    DateTimeOffset? CreatedAt = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null)
{
    public string Id { get; init; } = !string.IsNullOrWhiteSpace(Id)
        ? Id
        : throw new ArgumentException("A model needs its ID.", nameof(Id));

    /// <summary>The provider's own name for the model's line, such as Anthropic's "opus", where it gives one.</summary>
    public string? Family { get; init; }

    /// <summary>What the provider says the model can do (FR-CAT-01).</summary>
    public AiCapabilities Capabilities { get; init; } = AiCapabilities.Unknown;
}

/// <summary>
/// The capabilities FR-CAT-01 lists, as a provider reports them. Each is null where the provider doesn't
/// say, which isn't the same as false: the catalog fills the gaps from the configuration (FR-CAT-06).
/// </summary>
/// <param name="ImageInput">The model takes images in a prompt.</param>
/// <param name="StructuredOutput">The model can be held to a JSON schema.</param>
/// <param name="ToolUse">The model can call tools the request defines.</param>
/// <param name="LiveSearch">The model can use the provider's own web or X search.</param>
/// <param name="ReasoningEffort">A request can set how hard the model thinks.</param>
public sealed record AiCapabilities(
    bool? ImageInput = null,
    bool? StructuredOutput = null,
    bool? ToolUse = null,
    bool? LiveSearch = null,
    bool? ReasoningEffort = null)
{
    /// <summary>A provider that says nothing.</summary>
    public static AiCapabilities Unknown { get; } = new();
}

/// <summary>One page of a provider's model list.</summary>
/// <param name="NextPageToken">What asks for the next page, or null after the last.</param>
public sealed record AiModelPage(IReadOnlyList<AiModel> Models, string? NextPageToken = null);

/// <summary>A provider that has an adapter.</summary>
/// <param name="Name">The key everything else uses for it: the configuration section, the catalog's provider and the credential's.</param>
/// <param name="Enabled">AiProviders:&lt;Name&gt;:Enabled. A call to a disabled provider fails without being sent.</param>
public sealed record AiProviderInfo(string Name, bool Enabled);
