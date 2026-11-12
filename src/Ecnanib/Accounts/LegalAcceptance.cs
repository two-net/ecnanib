using Ecnanib.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecnanib.Accounts;

/// <summary>
/// The record that a user accepted one version of a document, and when, as a row of the
/// <c>legal_acceptances</c> table (FR-ACC-8).
/// </summary>
/// <remarks>
/// A row is written once and never changed. Accepting a later version adds a row, so what a user
/// accepted earlier stays on record.
/// </remarks>
public sealed class LegalAcceptance(Guid userId, LegalDocument document, string version, DateTimeOffset acceptedAt)
    : IUserOwned
{
    public Guid UserId { get; } = userId;

    public LegalDocument Document { get; } = document;

    public string Version { get; } = version;

    public DateTimeOffset AcceptedAt { get; } = acceptedAt;
}

internal sealed class LegalAcceptanceConfiguration : IEntityTypeConfiguration<LegalAcceptance>
{
    public void Configure(EntityTypeBuilder<LegalAcceptance> builder)
    {
        builder.ToTable("legal_acceptances");
        builder.HasKey(acceptance => new { acceptance.UserId, acceptance.Document, acceptance.Version });
        builder.Property(acceptance => acceptance.UserId);
        builder.Property(acceptance => acceptance.Document);
        builder.Property(acceptance => acceptance.Version);
        builder.Property(acceptance => acceptance.AcceptedAt);

        // The records belong to the account and go with it when it is deleted.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(acceptance => acceptance.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
