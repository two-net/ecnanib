using Ecnanib.AiProviderGateway;
using Ecnanib.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Catalog;

/// <summary>
/// Whether a model of the catalog is still offered by its provider (FR-CAT-5). A member is
/// stored by its name, so renaming one changes stored data and needs a migration.
/// </summary>
public enum AiModelStatus
{
    /// <summary>The provider lists the model, or has stopped listing it only lately.</summary>
    Active,

    /// <summary>
    /// The provider has not listed the model for long enough to take it for gone. The row stays,
    /// so that what was recorded about the model still points at it, and the model is active
    /// again when the provider lists it again.
    /// </summary>
    Unavailable,
}

/// <summary>
/// A model that a provider offers, as a row of the <c>ai_models</c> table: an entry of the
/// catalog (FR-CAT-4). The rows come from the providers' own lists of their models and from
/// nowhere else, and none is ever deleted (FR-CAT-1, FR-CAT-5). See docs/catalog.md.
/// </summary>
/// <remarks>
/// What a provider does not say about a model is null, which is unknown. Nothing is guessed.
/// </remarks>
/// <param name="id">Assigned by the application when the model is first seen.</param>
/// <param name="provider">The provider that offers the model.</param>
/// <param name="identifier">
/// The provider's identifier of the model, which a call to the model names it by. A model is
/// stored once for its provider and identifier.
/// </param>
/// <param name="displayName">The name the provider gives the model for people to read.</param>
/// <param name="contextTokens">The most tokens the model takes as input.</param>
/// <param name="outputTokens">The most tokens the model writes in one answer.</param>
/// <param name="releasedAt">When the provider released the model.</param>
/// <param name="deprecatedAt">When the provider deprecated the model.</param>
/// <param name="retiresAt">When the provider plans to stop serving the model.</param>
/// <param name="status">Whether the provider still offers the model.</param>
/// <param name="missedSyncs">How many syncs in a row, up to the latest, the provider's list lacked the model.</param>
/// <param name="firstSeenAt">When the sync started that first found the model.</param>
/// <param name="lastSeenAt">When the sync started that last found the model.</param>
public sealed class AiModel(
    Guid id,
    AiProviderCode provider,
    string identifier,
    string? displayName,
    int? contextTokens,
    int? outputTokens,
    DateTimeOffset? releasedAt,
    DateTimeOffset? deprecatedAt,
    DateTimeOffset? retiresAt,
    AiModelStatus status,
    int missedSyncs,
    DateTimeOffset firstSeenAt,
    DateTimeOffset lastSeenAt)
{
    public Guid Id { get; } = id;

    public AiProviderCode Provider { get; } = provider;

    public string Identifier { get; } = identifier;

    public string? DisplayName { get; } = displayName;

    public int? ContextTokens { get; } = contextTokens;

    public int? OutputTokens { get; } = outputTokens;

    public DateTimeOffset? ReleasedAt { get; } = releasedAt;

    public DateTimeOffset? DeprecatedAt { get; } = deprecatedAt;

    public DateTimeOffset? RetiresAt { get; } = retiresAt;

    public AiModelStatus Status { get; } = status;

    public int MissedSyncs { get; } = missedSyncs;

    public DateTimeOffset FirstSeenAt { get; } = firstSeenAt;

    public DateTimeOffset LastSeenAt { get; } = lastSeenAt;
}

internal sealed class AiModelConfiguration : IEntityTypeConfiguration<AiModel>
{
    public void Configure(EntityTypeBuilder<AiModel> builder)
    {
        builder.ToTable("ai_models");
        builder.HasKey(model => model.Id);

        // The application assigns the id, so a row can be written without asking the database for one.
        builder.Property(model => model.Id).ValueGeneratedNever();
        builder.Property(model => model.Provider);
        builder.Property(model => model.Identifier);

        // What the provider says about the model, each of which may be unknown.
        builder.Property(model => model.DisplayName);
        builder.Property(model => model.ContextTokens);
        builder.Property(model => model.OutputTokens);
        builder.Property(model => model.ReleasedAt);
        builder.Property(model => model.DeprecatedAt);
        builder.Property(model => model.RetiresAt);

        builder.Property(model => model.Status);
        builder.Property(model => model.MissedSyncs);
        builder.Property(model => model.FirstSeenAt);
        builder.Property(model => model.LastSeenAt);

        // A model is stored once (FR-CAT-4). The index also finds the models of a provider.
        builder.HasIndex(model => new { model.Provider, model.Identifier }).IsUnique();

        // A provider is a seeded row, which is never deleted while a model points at it.
        builder.HasOne<AiProvider>()
            .WithMany()
            .HasForeignKey(model => model.Provider)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
