using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Modules.Catalog;

/// <summary>
/// Whether one key could use one model at its last sync (SRS 6.1 ModelAvailability, FR-CAT-02). There's no
/// foreign key to <c>credentials</c> or <c>users</c>, because other modules own them: a sync removes the rows
/// of a deleted key, and account deletion (#51) removes a user's by <see cref="UserId"/>.
/// </summary>
internal sealed class ModelAvailability
{
    public required Guid CredentialId { get; init; }

    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    /// <summary>The key's owner, so a user's rows are found without asking Credentials (NFR-SEC-04).</summary>
    public required Guid UserId { get; init; }

    public bool Available { get; set; }

    /// <summary>
    /// When the sync that wrote this began asking the provider, not when it wrote. A result whose request
    /// began earlier is older, and never replaces this one.
    /// </summary>
    public DateTimeOffset CheckedAt { get; set; }
}

internal sealed class ModelAvailabilityConfiguration : IEntityTypeConfiguration<ModelAvailability>
{
    public void Configure(EntityTypeBuilder<ModelAvailability> builder)
    {
        builder.ToTable("model_availability");
        builder.HasKey(availability => new { availability.CredentialId, availability.Provider, availability.ModelId });
        builder.HasOne<Model>()
            .WithMany()
            .HasForeignKey(availability => new { availability.Provider, availability.ModelId })
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(availability => availability.UserId);
    }
}
