using System.Text.Json;
using Ecnanib.Ai.Abstractions;

namespace Ecnanib.Database.Entities;

/// <summary>
/// One entry of the per-provider model catalog (FR-SYNC-01). Shared by all users. The typed columns mirror
/// <c>AiModelInfo</c>; <see cref="Capabilities"/> keeps whatever else the provider returned.
/// </summary>
public sealed class ProviderModel
{
    public Guid Id { get; set; }

    public AiProvider Provider { get; set; }

    public required string ModelId { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Creation date reported by the provider; orders "most recently added" suggestions (FR-SYNC-05).</summary>
    public DateTimeOffset? ProviderCreatedAt { get; set; }

    public int? InputTokenLimit { get; set; }

    public int? OutputTokenLimit { get; set; }

    public bool? SupportsTextGeneration { get; set; }

    public JsonElement Capabilities { get; set; } = JsonValues.EmptyObject;

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public ModelStatus Status { get; set; } = ModelStatus.Available;

    /// <summary>Consecutive syncs that did not list the model; reaching the configured threshold deprecates it (FR-SYNC-03).</summary>
    public int MissedSyncs { get; set; }
}
