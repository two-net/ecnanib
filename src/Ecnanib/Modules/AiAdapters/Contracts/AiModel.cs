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
}

/// <summary>One page of a provider's model list.</summary>
/// <param name="NextPageToken">What asks for the next page, or null after the last.</param>
public sealed record AiModelPage(IReadOnlyList<AiModel> Models, string? NextPageToken = null);

/// <summary>A provider that has an adapter.</summary>
/// <param name="Name">The key everything else uses for it: the configuration section, the catalog's provider and the credential's.</param>
/// <param name="Enabled">AiProviders:&lt;Name&gt;:Enabled. A call to a disabled provider fails without being sent.</param>
public sealed record AiProviderInfo(string Name, bool Enabled);
