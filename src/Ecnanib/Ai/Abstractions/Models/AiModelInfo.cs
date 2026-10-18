using System.Text.Json;

namespace Ecnanib.Ai.Abstractions;

/// <summary>
/// A catalog entry (FR-SYNC-01). <see cref="Id"/> is directly usable as <see cref="AiRequest.Model"/>.
/// <see cref="SupportsTextGeneration"/> is derived only where the provider states it (null otherwise); lists are not
/// filtered. <see cref="Capabilities"/> is the provider's raw model object, kept verbatim for whatever metadata the
/// provider returns.
/// </summary>
public sealed record AiModelInfo(
    AiProvider Provider,
    string Id,
    string DisplayName,
    DateTimeOffset? CreatedAt,
    int? InputTokenLimit,
    int? OutputTokenLimit,
    bool? SupportsTextGeneration,
    JsonElement Capabilities);
