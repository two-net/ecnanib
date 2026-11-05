using Ecnanib.Modules.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// A model, as providers report it (SRS 6.1). Metadata from the configuration file isn't stored here: it's
/// applied as the catalog is read, so it changes with a restart, and administrators' overrides sit above it in
/// <see cref="ModelOverrideRow"/>. A field a provider stops sending keeps its last value.
/// </summary>
internal sealed class Model
{
    /// <summary>The longest <see cref="ModelStatus"/> name, with room to spare.</summary>
    public const int StatusMaxLength = 16;

    /// <summary>A row of <c>providers</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>The ID a request names it by, exactly as the provider lists it.</summary>
    public required string ModelId { get; init; }

    public string? DisplayName { get; set; }

    public string? Family { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    public int? ContextWindow { get; set; }

    public int? MaxOutputTokens { get; set; }

    public bool? ImageInput { get; set; }

    public bool? StructuredOutput { get; set; }

    public bool? ToolUse { get; set; }

    public bool? LiveSearch { get; set; }

    public bool? ReasoningEffort { get; set; }

    /// <summary>
    /// Deprecated and then Retired once providers stop listing it, and Active again when one lists it (FR-CAT-04).
    /// Never Hidden: hiding is an override, which leaves this alone.
    /// </summary>
    public ModelStatus Status { get; set; } = ModelStatus.Active;

    public required DateTimeOffset FirstSeenAt { get; init; }

    public required DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// When a sync of everyone's keys first found no key that could use it, which starts the grace period
    /// before it retires. Null while a key can.
    /// </summary>
    public DateTimeOffset? MissingSince { get; set; }
}

internal sealed class ModelConfiguration : IEntityTypeConfiguration<Model>
{
    public void Configure(EntityTypeBuilder<Model> builder)
    {
        builder.ToTable("models");

        // The natural key, so a role assignment (#24) names a model as the SRS does: provider and ID.
        builder.HasKey(model => new { model.Provider, model.ModelId });
        builder.Property(model => model.Status).HasConversion<string>().HasMaxLength(Model.StatusMaxLength);

        // Providers are seeded and never removed, so a model can't outlive its provider's row.
        builder.HasOne<Provider>().WithMany().HasForeignKey(model => model.Provider).OnDelete(DeleteBehavior.Restrict);
    }
}
